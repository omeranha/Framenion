using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace framenion.Src.Ocr;

/// <summary>A captured frame plus where its top-left sits in desktop coordinates.</summary>
public sealed class CapturedFrame(Mat image, int originX, int originY) : IDisposable
{
	public Mat Image { get; } = image;
	public int OriginX { get; } = originX;
	public int OriginY { get; } = originY;

	public void Dispose() => Image.Dispose();
}

public static class ScreenCapture
{
	/// <summary>
	/// Captures Warframe's client area when the game window can be found, so the reward layout
	/// ratios are measured against game content rather than the desktop. Falls back to the
	/// primary monitor.
	/// </summary>
	public static CapturedFrame Capture()
	{
		if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
			throw new PlatformNotSupportedException("Screen capture is only supported on Windows 7 or later.");

		if (TryGetGameClientRect(out int x, out int y, out int width, out int height)) {
			return new CapturedFrame(CaptureRegion(x, y, width, height), x, y);
		}

		int screenWidth = GetSystemMetrics(SM_CXSCREEN);
		int screenHeight = GetSystemMetrics(SM_CYSCREEN);
		return new CapturedFrame(CaptureRegion(0, 0, screenWidth, screenHeight), 0, 0);
	}

	private static bool TryGetGameClientRect(out int x, out int y, out int width, out int height)
	{
		x = y = width = height = 0;
		try {
			var handle = FindGameWindow();
			if (handle == IntPtr.Zero) return false;
			if (!GetClientRect(handle, out RECT client)) return false;

			width = client.Right - client.Left;
			height = client.Bottom - client.Top;
			if (width < 320 || height < 240) return false;

			var origin = new POINT { X = client.Left, Y = client.Top };
			if (!ClientToScreen(handle, ref origin)) return false;

			x = origin.X;
			y = origin.Y;
			return true;
		} catch {
			return false;
		}
	}

	private static IntPtr FindGameWindow()
	{
		var processes = Process.GetProcessesByName("Warframe.x64");
		try {
			return processes.Length == 0 ? IntPtr.Zero : processes[0].MainWindowHandle;
		} finally {
			foreach (var process in processes) {
				process.Dispose();
			}
		}
	}

	/// <summary>
	/// Brings Warframe to the front. The capture is a screen BitBlt, so anything sitting on top
	/// of the game would otherwise land in the image.
	/// </summary>
	public static bool TryFocusGameWindow()
	{
		try {
			var handle = FindGameWindow();
			if (handle == IntPtr.Zero) return false;

			if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
			return SetForegroundWindow(handle);
		} catch (Exception ex) {
			Log.Debug($"Could not focus the Warframe window: {ex.Message}");
			return false;
		}
	}

	private static Mat CaptureRegion(int x, int y, int width, int height)
	{
		var output = new Mat(height, width, MatType.CV_8UC4);

		IntPtr hScreenDC = GetDC(IntPtr.Zero);
		IntPtr hMemDC = CreateCompatibleDC(hScreenDC);
		IntPtr hBitmap = CreateCompatibleBitmap(hScreenDC, width, height);
		IntPtr hOld = SelectObject(hMemDC, hBitmap);

		BitBlt(hMemDC, 0, 0, width, height, hScreenDC, x, y, CopyPixelOperation.SourceCopy);

		var bmpData = new BITMAPINFO();
		bmpData.bmiHeader.biSize = Marshal.SizeOf<BITMAPINFOHEADER>();
		bmpData.bmiHeader.biWidth = width;
		bmpData.bmiHeader.biHeight = -height;
		bmpData.bmiHeader.biPlanes = 1;
		bmpData.bmiHeader.biBitCount = 32;
		bmpData.bmiHeader.biCompression = 0;

		_ = GetDIBits(hMemDC, hBitmap, 0, (uint)height, output.Data, ref bmpData, 0);

		SelectObject(hMemDC, hOld);
		DeleteObject(hBitmap);
		DeleteDC(hMemDC);
		ReleaseDC(IntPtr.Zero, hScreenDC);
		Cv2.CvtColor(output, output, ColorConversionCodes.BGRA2BGR);
		return output;
	}

	#region Win32
	private const int SM_CXSCREEN = 0;
	private const int SM_CYSCREEN = 1;
	private const int SW_RESTORE = 9;

	private enum CopyPixelOperation
	{
		SourceCopy = 0x00CC0020
	}

	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

	[DllImport("user32.dll")]
	private static extern bool IsIconic(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern bool ReleaseDC(IntPtr hWnd, IntPtr hDC);

	[DllImport("user32.dll")]
	private static extern IntPtr GetDC(IntPtr hWnd);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int nIndex);

	[DllImport("user32.dll")]
	private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

	[DllImport("user32.dll")]
	private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

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
	private static extern bool BitBlt(IntPtr hDestDC, int x, int y, int width, int height, IntPtr hSrcDC, int srcX, int srcY, CopyPixelOperation rop);

	[DllImport("gdi32.dll")]
	private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, IntPtr lpvBits, ref BITMAPINFO lpbi, uint uUsage);

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT
	{
		public int X;
		public int Y;
	}

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
