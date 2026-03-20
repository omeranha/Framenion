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
	}

	private async void SaveApply_Click(object? sender, RoutedEventArgs e)
	{
		settings.AppBackground = AppBackgroundBox.Text?.Trim() ?? settings.AppBackground;
		settings.PanelBackground = PanelBackgroundBox.Text?.Trim() ?? settings.PanelBackground;
		settings.ButtonBackground = ButtonBackgroundBox.Text?.Trim() ?? settings.ButtonBackground;
		settings.ButtonForeground = ButtonForegroundBox.Text?.Trim() ?? settings.ButtonForeground;
		settings.EnableNotifications = EnableNotificationsCheckBox.IsChecked ?? true;
		settings.ApplyToApplicationResources();

		try {
			await settings.SaveAsync();
			_ = ToastWindow.ShowToastAsync(this, "Settings", "Settings saved successfully.", TimeSpan.FromSeconds(5), ToastAnchor.TopRightOfOwnerWindow);
		} catch (Exception ex) {
			_ = ToastWindow.ShowToastAsync(this, "Error", $"Failed to save settings: {ex.Message}", TimeSpan.FromSeconds(5), ToastAnchor.TopRightOfOwnerWindow);
		}
	}

	private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
