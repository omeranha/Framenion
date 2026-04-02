using OpenCvSharp;
using Sdcb.PaddleOCR;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

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

	public static List<Reward> ReadRewards(Mat screenshot, PaddleOcrAll? engine)
	{
		var rewards = new List<Reward>();

		if (!OperatingSystem.IsWindows() || engine == null) return rewards;

		double uiScale = GameData.appSettings.UIScale / 100.0;
		int mostWidth = (int)(rewardsWidth * uiScale);
		int rewardsAreaWidth = (int)(rewardsWidth * uiScale);
		int rewardsAreaLeft = (screenshot.Width / 2) - (rewardsAreaWidth / 2);
		int rewardsAreaTop = screenshot.Height / 2 - (int)((rewardsY - rewardsHeight + itemNameHeight) * uiScale);
		int rewardsAreaBottom = screenshot.Height / 2 - (int)((rewardsY - rewardsHeight) * uiScale * 0.5);
		var rewardsRect = new Rect(rewardsAreaLeft, rewardsAreaTop, rewardsAreaWidth, rewardsAreaBottom - rewardsAreaTop);
		using var cropped = screenshot[rewardsRect].Clone();
		Cv2.Resize(cropped, cropped, new Size(), ocrScale, ocrScale, InterpolationFlags.Lanczos4);
		if (GameData.appSettings.DebugOCR) {
			Cv2.ImWrite(Path.Combine(GameData.appDataDir, "debug_itemnames.png"), cropped);
		}

		var itemNames = engine.Run(cropped);
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
			var validWords = col.Where(r => r.Text.Length >= 2 || r.Text == "&").ToList();
			if (validWords.Count == 0) continue;

			var itemName = string.Join(" ", validWords.Select(r => NormalizeItemName(r.Text))).Trim();
			if (string.IsNullOrEmpty(itemName) || itemName.Contains("Forma") || itemName.Contains("Riven") || itemName.Contains("Star")) continue;

			var rects = validWords.Select(r => r.Rect.BoundingRect()).ToList();
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

	public static string NormalizeItemName(string text)
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