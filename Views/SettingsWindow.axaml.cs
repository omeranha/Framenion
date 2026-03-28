using Avalonia.Controls;
using Avalonia.Interactivity;
using framenion.Src;
using System;
using System.Threading.Tasks;

namespace framenion;

public partial class SettingsWindow : Window
{
	private AppSettings settings = new();

	public SettingsWindow()
	{
		InitializeComponent();
		this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		Opened += async (_, _) => await LoadAsync();
	}

	private async Task LoadAsync()
	{
		settings = await AppSettings.LoadAsync();
		AppBackgroundBox.Text = settings.AppBackground;
		PanelBackgroundBox.Text = settings.PanelBackground;
		ButtonBackgroundBox.Text = settings.ButtonBackground;
		ButtonForegroundBox.Text = settings.ButtonForeground;
		ButtonSelected.Text = settings.ButtonSelected;
		EnableNotificationsCheckBox.IsChecked = settings.EnableNotifications;
		EnableEELogReadBox.IsChecked = settings.EnableEELogRead;
		EnableRelicOverlayBox.IsChecked = settings.EnableRelicOverlay;
		UiScaleNumeric.Value = settings.UIScale;
		DebugOCRBox.IsChecked = settings.DebugOCR;
	}

	private async void SaveApply_Click(object? sender, RoutedEventArgs e)
	{
		settings.AppBackground = AppBackgroundBox.Text?.Trim() ?? settings.AppBackground;
		settings.PanelBackground = PanelBackgroundBox.Text?.Trim() ?? settings.PanelBackground;
		settings.ButtonBackground = ButtonBackgroundBox.Text?.Trim() ?? settings.ButtonBackground;
		settings.ButtonForeground = ButtonForegroundBox.Text?.Trim() ?? settings.ButtonForeground;
		settings.ButtonSelected = ButtonSelected.Text?.Trim() ?? settings.ButtonSelected;
		settings.EnableNotifications = EnableNotificationsCheckBox.IsChecked ?? true;
		settings.EnableEELogRead = EnableEELogReadBox.IsChecked ?? true;
		settings.EnableRelicOverlay = EnableRelicOverlayBox.IsChecked ?? true;
		settings.UIScale = (int)(UiScaleNumeric.Value ?? settings.UIScale);
		settings.DebugOCR = DebugOCRBox.IsChecked ?? false;
		settings.ApplyToApplicationResources();

		try {
			await settings.SaveAsync();
			if (settings.EnableEELogRead) {
				GameData.logPollTimer.Start();
			} else {
				GameData.logPollTimer.Stop();
			}
			ToastWindow.ShowToast(this, "Settings", "Settings saved successfully.", TimeSpan.FromSeconds(3), ToastAnchor.TopRightOfOwnerWindow);
		} catch (Exception ex) {
			MessageBox.Show(this, "Error", $"Failed to save settings: {ex.Message}");
		}
	}

	private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
