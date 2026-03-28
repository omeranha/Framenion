using Avalonia.Input;
using Avalonia.Win32.Input;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace framenion.Src;

public sealed class WindowsKeyboardHook : IDisposable
{
	private const int WH_KEYBOARD_LL = 13;
	private const int WM_KEYDOWN = 0x0100;

	private static readonly LowLevelKeyboardProc KeyboardProc = HookCallback;
	private static IntPtr _keyboardHookId = IntPtr.Zero;

	private bool _hooked;

	public event Action<Key>? KeyEvent;

	public WindowsKeyboardHook()
	{
	}

	public void Hook()
	{
		if (_hooked) return;
		_keyboardHookId = SetHook(KeyboardProc);
		if (_keyboardHookId == IntPtr.Zero) {
			int error = Marshal.GetLastWin32Error();
			throw new System.ComponentModel.Win32Exception(error, "Failed to set keyboard hook.");
		}
		_hooked = true;
		Instance = this;
	}

	public void Unhook()
	{
		if (_hooked) {
			UnhookWindowsHookEx(_keyboardHookId);
			_hooked = false;
			Instance = null;
		}
	}

	private static WindowsKeyboardHook? Instance { get; set; }

	private static IntPtr SetHook(LowLevelKeyboardProc proc)
	{
		using var curProcess = Process.GetCurrentProcess();
		using var curModule = curProcess.MainModule!;
		return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
	}

	private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		if (nCode >= 0 && wParam == WM_KEYDOWN) {
			int vkCode = Marshal.ReadInt32(lParam);
			var key = KeyInterop.KeyFromVirtualKey(vkCode, 0);
			Avalonia.Threading.Dispatcher.UIThread.Post(() => Instance?.KeyEvent?.Invoke(key));
		}
		return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
	}

	private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool UnhookWindowsHookEx(IntPtr hhk);

	[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern IntPtr GetModuleHandle(string lpModuleName);

	public void Dispose()
	{
		Unhook();
	}
}
