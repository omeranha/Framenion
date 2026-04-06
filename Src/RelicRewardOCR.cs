using OpenCvSharp;
using Sdcb.PaddleOCR;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace framenion.Src;

public class RelicRewardOCR
{
	public class Reward
	{
		public required string ItemName;
		public Rect Rect;
	}

	private const int ocrScale = 2;

	// 1920x1080 100 UI scale reference values
	private const int rewardsWidth = 968;
	private const int rewardsHeight = 235;
	private const int rewardsY = 316;
	private const int itemNameHeight = 48;

	private static bool ocrRunning = false;

	public static void ReadRelicWindow()
	{
		if (!AppData.AppSettings.EnableRelicOverlay || ocrRunning) return;

		_ = Task.Run(async () => {
			try {
				ocrRunning = true;
				await Task.Delay(500);
				using var screenshot = ScreenCapture.Capture();
				var rewards = ReadRewards(screenshot);
				if (rewards.Count == 0) {
					ToastWindow.ShowToast("No rewards detected", "Could not detect any relic rewards.", TimeSpan.FromSeconds(5));
					return;
				}
				var rewardInfoTasks = rewards.Select(r => GameData.GetItemData(r.ItemName)).ToArray();
				var rewardInfos = await Task.WhenAll(rewardInfoTasks);

				var displayTasks = rewards.Zip(rewardInfos, (reward, info) => RelicRewardWindow.Display(info, reward.Rect.X, reward.Rect.Y + AppData.AppSettings.OverlayOffset, reward.Rect.Width));
				await Task.WhenAll(displayTasks);
			} catch (Exception ex) {
				MessageBox.Show("Error", $"Failed to read rewards: {ex.Message}");
			} finally {
				ocrRunning = false;
			}
		});
	}

	private static List<Reward> ReadRewards(Mat screenshot)
	{
		var rewards = new List<Reward>();

		if (!OperatingSystem.IsWindows() || AppData.PaddleEngine == null) return rewards;

		double uiScale = AppData.AppSettings.UIScale / 100.0;
		int mostWidth = (int)(rewardsWidth * uiScale);
		int rewardsAreaWidth = (int)(rewardsWidth * uiScale);
		int rewardsAreaLeft = (screenshot.Width / 2) - (rewardsAreaWidth / 2);
		int rewardsAreaTop = screenshot.Height / 2 - (int)((rewardsY - rewardsHeight + itemNameHeight) * uiScale);
		int rewardsAreaBottom = screenshot.Height / 2 - (int)((rewardsY - rewardsHeight) * uiScale * 0.5);
		var rewardsRect = new Rect(rewardsAreaLeft, rewardsAreaTop, rewardsAreaWidth, rewardsAreaBottom - rewardsAreaTop);
		using var cropped = screenshot[rewardsRect].Clone();
		Cv2.Resize(cropped, cropped, new Size(), ocrScale, ocrScale, InterpolationFlags.Lanczos4);
		if (AppData.AppSettings.DebugOCR) {
			Cv2.ImWrite(Path.Combine(AppData.AppDataDir, "debug_itemnames.png"), cropped);
		}

		var itemNames = AppData.PaddleEngine.Run(cropped);
		float xThreshold = 200f;
		var words = itemNames.Regions.Where(r => !string.IsNullOrWhiteSpace(r.Text))
			.Select(r => (region: r, x1: r.Rect.Center.X)).OrderBy(t => t.x1).ToList();

		var columns = new List<List<PaddleOcrResultRegion>>();
		foreach (var (region, x1) in words) {
			var col = columns.FirstOrDefault(c => {
				float avgX = c.Average(r => r.Rect.Center.X);
				return Math.Abs(avgX - x1) < xThreshold;
			});
			if (col == null) {
				col = [];
				columns.Add(col);
			}
			col.Add(region);
		}

		foreach (var col in columns) {
			col.Sort((a, b) => a.Rect.Center.Y.CompareTo(b.Rect.Center.Y));
		}

		foreach (var col in columns) {
			if (col.Count == 0) continue;

			var itemName = string.Join(" ", col.Select(r => NormalizeItemName(r.Text))).Trim();
			if (string.IsNullOrEmpty(itemName) || itemName.Contains("Forma") || itemName.Contains("Riven") || itemName.Contains("Star")) continue;

			itemName = LevenshteinItemName(itemName);
			if (itemName == null) continue;

			var rects = col.Select(r => r.Rect.BoundingRect()).ToList();
			int left = rects.Min(r => r.X); // leftmost edge of first word
			int right = rects.Max(r => r.X + r.Width); // rightmost edge of last word
			int bottom = rects.Max(r => r.Y + r.Height); // bottom edge of the lowest word in the column
			var last = rects[^1]; // the last word in the column
			var itemRect = new Rect(
				rewardsRect.X + (left / ocrScale),
				rewardsRect.Y + ((bottom - last.Height) / ocrScale),
				(right - left) / ocrScale,
				last.Height / ocrScale
			);
			rewards.Add(new Reward { ItemName = itemName, Rect = itemRect });
		}
		return rewards;
	}

	private static string NormalizeItemName(string text)
	{
		if (string.IsNullOrEmpty(text))
			return text;

		var builder = new StringBuilder(text.Length * 2);
		char previous = text[0];
		builder.Append(previous);
		for (int i = 1; i < text.Length; i++) {
			char c = text[i];

			bool isUpper = (uint)(c - 'A') <= ('Z' - 'A');
			bool prevIsLower = (uint)(previous - 'a') <= ('z' - 'a');
			if (isUpper && prevIsLower) {
				builder.Append(' ');
			}

			builder.Append(c);
			previous = c;
		}

		return builder.ToString();
	}

	private static string? LevenshteinItemName(string ocrText)
	{
		if (string.IsNullOrWhiteSpace(ocrText)) return null;

		var candidates = GameData.PrimeItems.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var exact = candidates.FirstOrDefault(candidate => string.Equals(candidate, ocrText, StringComparison.OrdinalIgnoreCase));
		if (exact != null) {
			return exact;
		}

		var scored = candidates.Select(candidate => (Name: candidate, Score: LevenshteinSimilarity(ocrText, candidate))).OrderByDescending(t => t.Score).ToList();
		if (scored.Count > 0 && scored[0].Score >= 0.80) {
			return scored[0].Name;
		}

		return null;
	}

	private static double LevenshteinSimilarity(string a, string b)
	{
		if (a == b) return 1.0;
		if (string.IsNullOrEmpty(a)) return string.IsNullOrEmpty(b) ? 1.0 : 0.0;
		if (string.IsNullOrEmpty(b)) return 0.0;

		int n = a.Length;
		int m = b.Length;
		var d = new int[n + 1, m + 1];
		for (int i = 0; i <= n; i++) d[i, 0] = i;
		for (int j = 0; j <= m; j++) d[0, j] = j;

		for (int i = 1; i <= n; i++) {
			for (int j = 1; j <= m; j++) {
				int cost = a[i - 1] == b[j - 1] ? 0 : 1;
				d[i, j] = Math.Min(
					Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
					d[i - 1, j - 1] + cost);
			}
		}

		int levenshtein = d[n, m];
		int max = Math.Max(n, m);
		return max == 0 ? 1.0 : 1.0 - (double)levenshtein / max;
	}
}

#region Win32
public static class ScreenCapture
{
	[DllImport("user32.dll")]
	private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);

	[DllImport("gdi32.dll")]
	private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteDC(IntPtr hDC);

	[DllImport("gdi32.dll")]
	private static extern IntPtr CreateCompatibleBitmap(IntPtr hDC, int width, int height);

	[DllImport("gdi32.dll")]
	private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

	[DllImport("gdi32.dll")]
	private static extern bool DeleteObject(IntPtr hObject);

	[DllImport("gdi32.dll")]
	private static extern bool BitBlt(
		IntPtr hDestDC,
		int x, int y,
		int width, int height,
		IntPtr hSrcDC,
		int srcX, int srcY,
		CopyPixelOperation rop);

	[DllImport("user32.dll")]
	private static extern IntPtr GetDC(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int nIndex);

	private const int SM_CXSCREEN = 0;
	private const int SM_CYSCREEN = 1;

	private enum CopyPixelOperation
	{
		SourceCopy = 0x00CC0020
	}

	public static Mat Capture()
	{
		if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
			throw new PlatformNotSupportedException("Screen capture is only supported on Windows 7 or later.");
		int width = GetSystemMetrics(SM_CXSCREEN);
		int height = GetSystemMetrics(SM_CYSCREEN);
		var output = new Mat(height, width, MatType.CV_8UC4);

		IntPtr hScreenDC = GetDC(IntPtr.Zero);
		IntPtr hMemDC = CreateCompatibleDC(hScreenDC);
		IntPtr hBitmap = CreateCompatibleBitmap(hScreenDC, width, height);
		IntPtr hOld = SelectObject(hMemDC, hBitmap);

		BitBlt(hMemDC, 0, 0, width, height, hScreenDC, 0, 0, CopyPixelOperation.SourceCopy);

		var bmpData = new BITMAPINFO();
		bmpData.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
		bmpData.bmiHeader.biWidth = width;
		bmpData.bmiHeader.biHeight = -height;
		bmpData.bmiHeader.biPlanes = 1;
		bmpData.bmiHeader.biBitCount = 32;
		bmpData.bmiHeader.biCompression = 0;

		_ = GetDIBits(hMemDC, hBitmap, 0, (uint)height, output.Data, ref bmpData, 0);

		// cleanup
		SelectObject(hMemDC, hOld);
		DeleteObject(hBitmap);
		DeleteDC(hMemDC);
		ReleaseDC(IntPtr.Zero, hScreenDC);
		Cv2.CvtColor(output, output, ColorConversionCodes.BGRA2BGR);
		return output;
	}

	[DllImport("gdi32.dll")]
	private static extern int GetDIBits(
		IntPtr hdc,
		IntPtr hbmp,
		uint uStartScan,
		uint cScanLines,
		IntPtr lpvBits,
		ref BITMAPINFO lpbi,
		uint uUsage);

	[StructLayout(LayoutKind.Sequential)]
	private struct BITMAPINFO
	{
		public BITMAPINFOHEADER bmiHeader;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
		public uint[] bmiColors;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct BITMAPINFOHEADER
	{
		public int biSize;
		public int biWidth;
		public int biHeight;
		public short biPlanes;
		public short biBitCount;
		public int biCompression;
		public int biSizeImage;
		public int biXPelsPerMeter;
		public int biYPelsPerMeter;
		public int biClrUsed;
		public int biClrImportant;
	}

	#endregion
}