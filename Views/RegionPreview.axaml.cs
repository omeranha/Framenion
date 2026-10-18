using Avalonia;
using Avalonia.Controls;
using framenion.Src;
using framenion.Src.Ocr;
using System;
using System.Threading.Tasks;

namespace framenion;

/// <summary>
/// Briefly outlines a scan region on screen so the user can confirm it lines up with the game.
/// </summary>
public partial class RegionPreviewWindow : Window
{
	private static readonly TimeSpan visibleFor = TimeSpan.FromSeconds(2.5);

	public string Caption { get; private set; } = "";

	public RegionPreviewWindow()
	{
		InitializeComponent();
		DataContext = this;
		IsHitTestVisible = false;
	}

	public static async Task FlashAsync(ScanRegion region, string caption)
	{
		// Regions are fractions of the captured frame, so the preview has to be placed against
		// the same frame: Warframe's client area when it is running, else the primary screen.
		int originX = 0, originY = 0, frameWidth, frameHeight;
		try {
			using var frame = ScreenCapture.Capture();
			originX = frame.OriginX;
			originY = frame.OriginY;
			frameWidth = frame.Image.Width;
			frameHeight = frame.Image.Height;
		} catch (Exception ex) {
			Log.Warn("Could not capture a frame to anchor the region preview.", ex);
			return;
		}

		var window = new RegionPreviewWindow {
			Caption = $"{caption}  —  {region}",
			WindowStartupLocation = WindowStartupLocation.Manual,
		};
		try {
			window.ShowActivated = false;
		} catch (Exception ex) {
			Log.Debug($"Could not suppress activation for the region preview: {ex.Message}");
		}

		int x = originX + (int)Math.Round(region.X * frameWidth);
		int y = originY + (int)Math.Round(region.Y * frameHeight);
		int width = Math.Max(8, (int)Math.Round(region.Width * frameWidth));
		int height = Math.Max(8, (int)Math.Round(region.Height * frameHeight));

		window.Show();
		var scaling = window.Screens.ScreenFromWindow(window)?.Scaling ?? 1.0;
		window.Position = new PixelPoint(x, y);
		window.Width = width / scaling;
		window.Height = height / scaling;

		await Task.Delay(visibleFor);
		window.Close();
	}
}
