using Avalonia.Controls;
using System;
using System.Runtime.InteropServices;

namespace framenion.Src;

/// <summary>
/// Keeps native window title bars dark.
///
/// Avalonia asks Windows for a dark title bar on its own, but only on Windows 11 (build 22000+)
/// and only when the window's theme variant already resolves to dark, which is why the app sets
/// RequestedThemeVariant="Dark". On Windows 10 it never makes the call, so the attribute has to
/// be set by hand here.
/// </summary>
public static class WindowChrome
{
	private const int DwmwaUseImmersiveDarkMode = 20;
	private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

	private const uint SwpNoSize = 0x0001;
	private const uint SwpNoMove = 0x0002;
	private const uint SwpNoZOrder = 0x0004;
	private const uint SwpNoActivate = 0x0010;
	private const uint SwpFrameChanged = 0x0020;

	[DllImport("dwmapi.dll", PreserveSig = true)]
	private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

	[DllImport("user32.dll")]
	private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

	/// <summary>
	/// Applies the dark title bar now and again once the window opens. Windows only repaints the
	/// caption when the frame changes, so setting the attribute on an already-visible window can
	/// leave a light title bar behind; applying it before the first show avoids that, and the
	/// second pass covers the case where the native handle was not ready yet.
	/// </summary>
	public static void UseDarkTitleBar(this Window window)
	{
		Apply(window);
		window.Opened += (_, _) => Apply(window);
	}

	private static void Apply(Window window)
	{
		if (!OperatingSystem.IsWindows()) return;

		try {
			if (window.TryGetPlatformHandle() is not { } handle || handle.Handle == IntPtr.Zero) return;

			int useDark = 1;
			// Windows 11 and Windows 10 2004+ use attribute 20; earlier 20H1 builds need 19.
			if (DwmSetWindowAttribute(handle.Handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0) {
				DwmSetWindowAttribute(handle.Handle, DwmwaUseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));
			}

			SetWindowPos(handle.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
		} catch (Exception ex) {
			Log.Warn("Could not switch the window title bar to dark mode.", ex);
		}
	}
}
