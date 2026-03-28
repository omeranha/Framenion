using Avalonia;
using Avalonia.Markup.Xaml.Templates;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Rect = OpenCvSharp.Rect;

namespace framenion.Src;

public class RelicRewardOCR
{
	public class Reward
	{
		public required string ItemName;
		public Rect Rect;
	}

	public static List<Reward> ReadRewards(Mat screenshot, PaddleOcrAll? engine)
	{
		var rewards = new List<Reward>();
		if (!OperatingSystem.IsWindows()) return rewards;
		if (engine == null) return rewards;

		var topHalfRect = new Rect(0, 0, screenshot.Width, screenshot.Height / 2);
		screenshot = screenshot[topHalfRect];
		if (GameData.appSettings.DebugOCR) {
			Cv2.ImWrite(Path.Combine(GameData.appDataDir, "debug_screenshot.png"), screenshot);
		}

		Mat template = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "assets", "template.png"), ImreadModes.Grayscale);
		var rewardsY = DetectRewardsY(screenshot, template);
		if (rewardsY == -1) return rewards;

		int rewardsHeight = (int)(250 * (GameData.appSettings.UIScale / 100.0));
		var rewardsRect = new Rect(0, rewardsY, screenshot.Width, rewardsHeight);
		screenshot = screenshot[rewardsRect];

		int rewardsYOffset = topHalfRect.Y + rewardsRect.Y;

		var textRect = new Rect(0, screenshot.Height / 2, screenshot.Width, screenshot.Height - screenshot.Height / 2);
		var textCrop = screenshot[textRect];

		if (GameData.appSettings.DebugOCR) {
			Cv2.ImWrite(Path.Combine(GameData.appDataDir, "debug_itemnames.png"), textCrop);
		}
		var itemNames = engine.Run(textCrop);

		static float GetX1(RotatedRect rect)
		{
			var pts = rect.Points();
			return pts.Min(p => p.X);
		}

		float xThreshold = 150f;
		var words = itemNames.Regions.Where(r => !string.IsNullOrWhiteSpace(r.Text))
			.Select(r => (region: r, x1: GetX1(r.Rect))).OrderBy(t => t.x1).ToList();

		var columns = new List<List<PaddleOcrResultRegion>>();
		foreach (var (region, x1) in words) {
			var col = columns.FirstOrDefault(c => Math.Abs(GetX1(c[0].Rect) - x1) < xThreshold);
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
			var itemName = string.Join(" ", col.Select(r => r.Text)).Trim();
			if (!string.IsNullOrEmpty(itemName)) {
				if (itemName.Contains("Forma") || itemName.Contains("Riven") || itemName.Contains("Star")) {
					continue;
				}

				var regionRect = col[0].Rect.BoundingRect();
				var adjustedRect = new Rect(
					regionRect.X + rewardsRect.X,
					regionRect.Y + rewardsYOffset,
					regionRect.Width,
					regionRect.Height
				);
				rewards.Add(new Reward { ItemName = itemName, Rect = adjustedRect });
			}
		}
		return rewards;
	}

	public static int DetectRewardsY(Mat input, Mat template)
	{
		var detections = new List<((int x, int y), double score)>();
		using var edgeTemplate = new Mat();
		using var grayInput = new Mat();
		Cv2.CvtColor(input, grayInput, ColorConversionCodes.BGR2GRAY);
		Cv2.Canny(template, edgeTemplate, 50, 150);

		using var current = grayInput.Clone();
		double scale = 1.0;
		const double scaleStep = 0.8;
		const double threshold = 0.5;
		const double minAspect = 0.6;
		const double maxAspect = 1.5;
		int minY = (int)(input.Rows * 0.1);
		while (current.Width >= template.Width && current.Height >= template.Height) {
			using var edgeCurrent = new Mat();
			using var result = new Mat();
			Cv2.Canny(current, edgeCurrent, 50, 150);
			Cv2.MatchTemplate(edgeCurrent, edgeTemplate, result, TemplateMatchModes.CCoeffNormed);
			for (int y = 0; y < result.Rows; y++) {
				for (int x = 0; x < result.Cols; x++) {
					float score = result.At<float>(y, x);
					if (score < threshold) continue;

					int rx = (int)(x / scale);
					int ry = (int)(y / scale);
					int rw = (int)(template.Width / scale);
					int rh = (int)(template.Height / scale);

					if (ry < minY) continue;
					float aspect = (float)rw / rh;
					if (aspect < minAspect || aspect > maxAspect) continue;

					detections.Add(((rx, ry), score));
				}
			}
			scale *= scaleStep;
			Cv2.Resize(current, current, new OpenCvSharp.Size(), scaleStep, scaleStep);
		}

		if (detections.Count == 0) return -1;

		return detections.Select(d => {
			double distanceToCenter = Math.Abs(d.Item1.x - (input.Width / 2.0));
			double finalScore = d.score - (distanceToCenter * 0.001);
			return (d.Item1.y, finalScore);
		}).OrderByDescending(x => x.finalScore).First().y;
	}
}

public static class ScreenCapture
{
	#region Win32

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

		GetDIBits(hMemDC, hBitmap, 0, (uint)height, output.Data, ref bmpData, 0);

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