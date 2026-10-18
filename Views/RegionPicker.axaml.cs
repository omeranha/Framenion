using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using framenion.Src;
using framenion.Src.Ocr;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mat = OpenCvSharp.Mat;
using Cv2 = OpenCvSharp.Cv2;

namespace framenion;

/// <summary>
/// Full-screen region selector drawn over a live capture. Returns the selection as fractions of
/// the captured frame, which is what the scanners consume.
/// </summary>
public partial class RegionPickerWindow : Window
{
	private readonly Bitmap? shot;
	private readonly int frameWidth;
	private readonly int frameHeight;

	private Point anchor;
	private bool dragging;
	private ScanRegion selection;
	private ScanRegion? result;

	public string Hint { get; } = "";

	public RegionPickerWindow()
	{
		InitializeComponent();
		DataContext = this;
	}

	private RegionPickerWindow(Bitmap shot, int frameWidth, int frameHeight, ScanRegion initial, string hint)
	{
		this.shot = shot;
		this.frameWidth = frameWidth;
		this.frameHeight = frameHeight;
		selection = initial;
		Hint = hint;

		InitializeComponent();
		DataContext = this;
		Screenshot.Source = shot;
	}

	/// <summary>
	/// Brings Warframe to the front, takes every Framenion window out of shot, captures, and
	/// then lets the user drag a box over the result. Returns null when cancelled.
	/// </summary>
	public static async Task<ScanRegion?> PickAsync(ScanRegionKind kind, ScanRegion initial)
	{
		Bitmap shot;
		int width, height;

		var parked = ParkWindows();
		try {
			// The capture is a screen BitBlt, so the game has to actually be the top window.
			if (!ScreenCapture.TryFocusGameWindow()) {
				Log.Info("Warframe window not found; capturing the primary screen instead.");
			}

			// Give the compositor time to bring the game forward and repaint.
			await Task.Delay(500);

			using var frame = ScreenCapture.Capture();
			width = frame.Image.Width;
			height = frame.Image.Height;
			shot = ToBitmap(frame.Image);
		} catch (Exception ex) {
			Log.Error("Could not capture the screen for the region picker.", ex);
			return null;
		} finally {
			RestoreWindows(parked);
		}

		var picker = new RegionPickerWindow(shot, width, height, initial, ScanRegions.Hint(kind)) {
			Title = $"Select: {ScanRegions.Label(kind)}",
		};

		var completion = new TaskCompletionSource<ScanRegion?>(TaskCreationOptions.RunContinuationsAsynchronously);
		picker.Closed += (_, _) => {
			shot.Dispose();
			completion.TrySetResult(picker.result);
		};

		picker.Show();
		picker.Activate();
		picker.Focus();
		return await completion.Task;
	}

	/// <summary>
	/// Moves every visible Framenion window far off-screen. Hiding would be cleaner but one of
	/// them is shown as a modal dialog, and hiding that breaks its modal state.
	/// </summary>
	private static List<(Window Window, PixelPoint Position)> ParkWindows()
	{
		var parked = new List<(Window, PixelPoint)>();
		if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return parked;

		foreach (var window in desktop.Windows.ToArray()) {
			if (!window.IsVisible) continue;

			parked.Add((window, window.Position));
			window.Position = new PixelPoint(-30000, -30000);
		}
		return parked;
	}

	private static void RestoreWindows(List<(Window Window, PixelPoint Position)> parked)
	{
		foreach (var (window, position) in parked) {
			try {
				window.Position = position;
			} catch (Exception ex) {
				Log.Debug($"Could not restore a window position: {ex.Message}");
			}
		}
	}

	private static Bitmap ToBitmap(Mat image)
	{
		Cv2.ImEncode(".png", image, out byte[] encoded);
		using var stream = new MemoryStream(encoded);
		return new Bitmap(stream);
	}

	protected override void OnOpened(EventArgs e)
	{
		base.OnOpened(e);
		// Cover the monitor the capture came from.
		var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
		if (screen != null) {
			Position = screen.Bounds.Position;
			Width = screen.Bounds.Width / screen.Scaling;
			Height = screen.Bounds.Height / screen.Scaling;
		}
		WindowState = WindowState.FullScreen;
		DrawSelection();
	}

	/// <summary>Where the letterboxed image actually sits inside its container.</summary>
	private Rect ImageBounds()
	{
		var bounds = SurfaceHost.Bounds;
		if (frameWidth <= 0 || frameHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0) return default;

		double scale = Math.Min(bounds.Width / frameWidth, bounds.Height / frameHeight);
		double width = frameWidth * scale;
		double height = frameHeight * scale;
		return new Rect((bounds.Width - width) / 2, (bounds.Height - height) / 2, width, height);
	}

	private Point ToFraction(Point position)
	{
		var image = ImageBounds();
		if (image.Width <= 0 || image.Height <= 0) return new Point(0, 0);

		return new Point(
			Math.Clamp((position.X - image.X) / image.Width, 0, 1),
			Math.Clamp((position.Y - image.Y) / image.Height, 0, 1));
	}

	private void OnSurfacePressed(object? sender, PointerPressedEventArgs e)
	{
		anchor = ToFraction(e.GetPosition(SurfaceHost));
		dragging = true;
		selection = new ScanRegion(anchor.X, anchor.Y, 0, 0);
		DrawSelection();
	}

	private void OnSurfaceMoved(object? sender, PointerEventArgs e)
	{
		if (!dragging) return;

		var current = ToFraction(e.GetPosition(SurfaceHost));
		selection = new ScanRegion(
			Math.Min(anchor.X, current.X),
			Math.Min(anchor.Y, current.Y),
			Math.Abs(current.X - anchor.X),
			Math.Abs(current.Y - anchor.Y));
		DrawSelection();
	}

	private void OnSurfaceReleased(object? sender, PointerReleasedEventArgs e)
	{
		dragging = false;
		DrawSelection();
	}

	private void DrawSelection()
	{
		var image = ImageBounds();
		if (image.Width <= 0) return;

		Canvas.SetLeft(SelectionBox, image.X + (selection.X * image.Width));
		Canvas.SetTop(SelectionBox, image.Y + (selection.Y * image.Height));
		SelectionBox.Width = Math.Max(0, selection.Width * image.Width);
		SelectionBox.Height = Math.Max(0, selection.Height * image.Height);
		SelectionBox.IsVisible = selection.Width > 0 && selection.Height > 0;

		int pixelWidth = (int)Math.Round(selection.Width * frameWidth);
		int pixelHeight = (int)Math.Round(selection.Height * frameHeight);
		ReadoutText.Text = selection.IsValid
			? $"{selection}   ({pixelWidth} x {pixelHeight} px)"
			: "Drag a box over the area to scan";
		AcceptButton.IsEnabled = selection.IsValid;
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		base.OnKeyDown(e);
		if (e.Key == Key.Escape) {
			Accept(null);
		} else if (e.Key == Key.Enter && selection.IsValid) {
			Accept(selection.Clamped());
		}
	}

	private void OnAcceptClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		if (selection.IsValid) Accept(selection.Clamped());
	}

	private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Accept(null);

	/// <summary>Shown non-modally, so the result is carried on the instance rather than by Close(result).</summary>
	private void Accept(ScanRegion? picked)
	{
		result = picked;
		Close();
	}
}
