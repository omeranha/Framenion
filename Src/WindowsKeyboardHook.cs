using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Win32.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace framenion.Src;

/// <summary>
/// Global hotkey listener. The hotkey is swallowed rather than passed on, so neither Windows nor
/// the game also acts on it: the default of Print Screen would otherwise take a screenshot
/// </summary>
public sealed class WindowsKeyboardHook : IDisposable
{
	private const int WH_KEYBOARD_LL = 13;
	private const int WM_KEYDOWN = 0x0100;
	private const int WM_KEYUP = 0x0101;
	private const int WM_SYSKEYDOWN = 0x0104;
	private const int WM_SYSKEYUP = 0x0105;

	/// <summary>Returned instead of chaining the hook, which drops the key entirely.</summary>
	private static readonly IntPtr swallow = new(1);

	private static readonly LowLevelKeyboardProc keyboardProc = HookCallback;
	private static IntPtr hookId = IntPtr.Zero;
	private static WindowsKeyboardHook? instance;

	/// <summary>Keys seen held down, so an auto-repeat does not count as a new press.</summary>
	private readonly HashSet<Key> held = [];

	/// <summary>The key that triggers a scan.</summary>
	public Key Hotkey { get; set; } = Key.PrintScreen;

	/// <summary>Raised once per press of <see cref="Hotkey"/>, never on auto-repeat.</summary>
	public event Action? HotkeyPressed;

	/// <summary>Raised with the next key pressed while <see cref="IsCapturing"/> is set.</summary>
	public event Action<Key>? KeyCaptured;

	/// <summary>
	/// While set, the next key press is reported through <see cref="KeyCaptured"/> instead of
	/// being matched against the hotkey. Used by the settings window to record a new binding.
	/// </summary>
	public bool IsCapturing { get; set; }

	public bool IsHooked { get; private set; }

	public void Hook()
	{
		if (IsHooked) return;

		hookId = SetHook(keyboardProc);
		if (hookId == IntPtr.Zero) {
			throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Failed to set keyboard hook.");
		}
		IsHooked = true;
		instance = this;
	}

	public void Unhook()
	{
		if (!IsHooked) return;

		UnhookWindowsHookEx(hookId);
		hookId = IntPtr.Zero;
		IsHooked = false;
		instance = null;
		held.Clear();
	}

	/// <summary>Human-readable form of a binding, for the settings button.</summary>
	public static string Describe(Key key) => key switch {
		Key.PrintScreen => "Print Screen",
		Key.None => "None",
		_ => key.ToString(),
	};

	private static IntPtr SetHook(LowLevelKeyboardProc proc)
	{
		using var process = Process.GetCurrentProcess();
		using var module = process.MainModule!;
		return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
	}

	private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
	{
		var self = instance;
		if (nCode < 0 || self == null) return CallNextHookEx(hookId, nCode, wParam, lParam);

		int message = (int)wParam;
		bool down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
		bool up = message is WM_KEYUP or WM_SYSKEYUP;
		if (!down && !up) return CallNextHookEx(hookId, nCode, wParam, lParam);

		var key = KeyInterop.KeyFromVirtualKey(Marshal.ReadInt32(lParam), 0);
		if (key == Key.None) return CallNextHookEx(hookId, nCode, wParam, lParam);

		if (self.IsCapturing) {
			if (down && self.held.Add(key)) {
				self.IsCapturing = false;
				Dispatcher.UIThread.Post(() => self.KeyCaptured?.Invoke(key));
			} else if (up) {
				self.held.Remove(key);
			}
			// Nothing else should react to the key being recorded, including the game.
			return swallow;
		}

		if (key != self.Hotkey) return CallNextHookEx(hookId, nCode, wParam, lParam);

		// Holding a key repeats the key-down message, so only the first one counts. Print Screen
		// is the exception: Windows delivers it as a key-up with no preceding key-down, so a
		// key-up for a key that was never seen going down is the press.
		bool pressed = down ? self.held.Add(key) : !self.held.Remove(key);

		// Posted rather than invoked: a hook callback blocks the whole input queue while it runs.
		if (pressed) Dispatcher.UIThread.Post(() => self.HotkeyPressed?.Invoke());
		return swallow;
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

	public void Dispose() => Unhook();
}
