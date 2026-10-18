using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using framenion.Src;
using System;
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
				// Settings must be in place before the window is constructed: the monitor and
				// the reward overlay both read them on the way up.
				AppData.AppSettings = AppSettings.Load();
				AppData.AppSettings.ApplyToApplicationResources();

				// Closing the window only hides it, so the app has to be told explicitly to go.
				desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
				desktop.ShutdownRequested += (_, _) => AppData.IsShuttingDown = true;

				desktop.MainWindow = new MainWindow();
				AppData.MainWindow = desktop.MainWindow;
			}

			base.OnFrameworkInitializationCompleted();
		}

		/// <summary>Brings the window back from the tray.</summary>
		public void Open()
		{
			var mainWindow = AppData.MainWindow;
			if (mainWindow == null) return;

			mainWindow.Show();
			if (mainWindow.WindowState == WindowState.Minimized) {
				mainWindow.WindowState = WindowState.Normal;
			}
			mainWindow.Activate();
		}

		public void Exit()
		{
			AppData.IsShuttingDown = true;

			// App.axaml binds the tray menu to its own App instance, whose ApplicationLifetime
			// is null; the framework's instance is the one that can actually shut down. With
			// ShutdownMode.OnExplicitShutdown, closing the last window is not enough.
			if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
				desktop.Shutdown();
				return;
			}

			Log.Warn("No desktop lifetime available; closing the window and exiting directly.");
			AppData.MainWindow?.Close();
			Environment.Exit(0);
		}
	}
}