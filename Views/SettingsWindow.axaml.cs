using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using framenion.Src;
using System;
using System.Threading.Tasks;

namespace framenion;

public partial class SettingsWindow : Window
{
	private AppSettings settings = new();

	/// <summary>Held until Save, so cancelling the dialog leaves the live binding alone.</summary>
	private Key pendingHotkey = Key.PrintScreen;

	public SettingsWindow()
	{
		InitializeComponent();
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Opened += async (_, _) => await LoadAsync();
		// Leaving the hook in capture mode would swallow every keystroke system-wide.
		Closed += (_, _) => CancelCapture();
	}

	private async Task LoadAsync()
	{
		settings = await AppSettings.LoadAsync();
		AccentColorBox.Text = settings.AccentColor;
		EnableNotificationsCheckBox.IsChecked = settings.EnableNotifications;
		EnableEELogReadBox.IsChecked = settings.EnableEELogRead;
		EnableRelicOverlayBox.IsChecked = settings.EnableRelicOverlay;
		EnableRivenOverlayBox.IsChecked = settings.EnableRivenOverlay;
		UiScaleNumeric.Value = settings.UIScale;
		DebugOCRBox.IsChecked = settings.DebugOCR;
		OverlayOffset.Value = settings.OverlayOffset;
		pendingHotkey = settings.Hotkey;
		HotkeyButton.Content = WindowsKeyboardHook.Describe(pendingHotkey);
	}

	private void OnHotkeyClick(object? sender, RoutedEventArgs e)
	{
		var hook = AppData.KeyboardHook;
		if (!hook.IsHooked) {
			MessageBox.Show("Hotkey unavailable", $"The global keyboard hook is not installed, so a key cannot be recorded.\n\nSee {Log.Path}");
			return;
		}

		if (hook.IsCapturing) {
			CancelCapture();
			return;
		}

		HotkeyButton.Content = "Press a key…";
		hook.KeyCaptured += OnKeyCaptured;
		hook.IsCapturing = true;
	}

	private void OnKeyCaptured(Key key)
	{
		AppData.KeyboardHook.KeyCaptured -= OnKeyCaptured;
		if (key != Key.Escape) pendingHotkey = key;
		HotkeyButton.Content = WindowsKeyboardHook.Describe(pendingHotkey);
	}

	private void CancelCapture()
	{
		var hook = AppData.KeyboardHook;
		if (!hook.IsCapturing) return;

		hook.IsCapturing = false;
		hook.KeyCaptured -= OnKeyCaptured;
		HotkeyButton.Content = WindowsKeyboardHook.Describe(pendingHotkey);
	}

	private async void SaveApply_Click(object? sender, RoutedEventArgs e)
	{
		settings.AccentColor = AccentColorBox.Text?.Trim() ?? settings.AccentColor;
		settings.EnableNotifications = EnableNotificationsCheckBox.IsChecked ?? true;
		settings.EnableEELogRead = EnableEELogReadBox.IsChecked ?? true;
		settings.EnableRelicOverlay = EnableRelicOverlayBox.IsChecked ?? true;
		settings.EnableRivenOverlay = EnableRivenOverlayBox.IsChecked ?? true;
		settings.UIScale = (int)(UiScaleNumeric.Value ?? settings.UIScale);
		settings.DebugOCR = DebugOCRBox.IsChecked ?? false;
		settings.OverlayOffset = (int)(OverlayOffset.Value ?? settings.OverlayOffset);
		settings.Hotkey = pendingHotkey;
		settings.ApplyToApplicationResources();
		// Publish the new values, otherwise nothing picks them up until the next launch.
		AppData.AppSettings = settings;
		AppData.KeyboardHook.Hotkey = pendingHotkey;

		var originalBackground = SaveButton.Background;
		try {
			await settings.SaveAsync();
			if (settings.EnableEELogRead) {
				AppData.Monitor?.Start();
			} else {
				AppData.Monitor?.Stop();
			}
			SaveButton.Content = "Saved";
			SaveButton.Background = Brushes.LimeGreen;
			await Task.Delay(2000);
			// The window can be closed while the confirmation is showing.
			if (!IsVisible) return;
			SaveButton.Content = "Save";
			SaveButton.Background = originalBackground;
		} catch (Exception ex) {
			Log.Error("Failed to save settings.", ex);
			MessageBox.Show("Error", $"Failed to save settings: {ex.Message}");
		}
	}

	private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

	private async void OnScanRegionsClick(object? sender, RoutedEventArgs e)
	{
		try {
			await new ScanRegionsWindow().ShowDialog(this);
		} catch (Exception ex) {
			Log.Error("Could not open the scan regions editor.", ex);
			MessageBox.Show("Error", $"Failed to open scan regions: {ex.Message}");
		}
	}
}
