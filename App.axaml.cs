using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using framenion.Src;
using System.Threading.Tasks;

namespace framenion
{
	public partial class App : Application
	{
		public override void Initialize()
		{
			AvaloniaXamlLoader.Load(this);
		}

		public override void OnFrameworkInitializationCompleted()
		{
			if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
			{
				desktop.MainWindow = new MainWindow();
				_ = LoadAndApplySettingsAsync();
			}

			base.OnFrameworkInitializationCompleted();
		}

		private static async Task LoadAndApplySettingsAsync()
		{
			var settings = await AppSettings.LoadAsync();
			settings.ApplyToApplicationResources();
			AppData.AppSettings = settings;
		}

		public void ToggleWindow()
		{
			if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
				var mw = desktop.MainWindow;
				if (mw == null) return;
				if (mw.IsVisible) {
					mw.Hide();
				} else {
					mw.Show();
					mw.WindowState = WindowState.Normal;
				}
			}
		}

		public void Exit()
		{
			if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
				desktop.MainWindow?.Close();
			}
		}
	}
}