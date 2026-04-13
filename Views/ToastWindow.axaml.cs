using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using framenion.Src;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace framenion;

public enum ToastAnchor
{
	BottomRight,
	TopRight,
}

public partial class ToastWindow : Window
{
	private static readonly List<ToastWindow> activeToasts = [];
	private const int StackSpacing = 8;

	public string ToastTitle { get; } = string.Empty;
	public string Body { get; } = string.Empty;

	private const int DefaultMargin = 12;
	private const int SlideDurationMs = 250;

	public ToastWindow()
	{
		InitializeComponent();
		DataContext = this;
	}

	public ToastWindow(string title, string body, Bitmap? icon = null)
	{
		InitializeComponent();
		ToastTitle = title;
		Body = body;
		if (icon != null) {
			IconImage.Source = icon;
		}

		DataContext = this;
		RenderTransform = new TranslateTransform();
		RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
		PointerPressed += async (_, _) => await DismissAsync();
	}

	public static async void Show(string title, string body, ToastAnchor? anchor = null, TimeSpan ? duration = null, Bitmap? icon = null)
	{
		Dispatcher.UIThread.Post(async () => {
			var owner = AppData.MainWindow;
			if (owner == null) return;

			duration ??= TimeSpan.FromSeconds(5);

			var toast = new ToastWindow(title, body, icon);
			toast.Opened += async (_, _) => {
				lock (activeToasts) {
					activeToasts.Add(toast);
				}

				toast.ApplyStackedPosition(anchor ?? ToastAnchor.BottomRight);
				var deltaX = toast.GetSlideDistance();
				await CreateSlideAnimation(deltaX, SlideDurationMs, slideIn: true).RunAsync(toast);
			};

			toast.Closed += (_, _) => {
				lock (activeToasts) {
					activeToasts.Remove(toast);
					foreach (var toast in activeToasts) {
						toast.ApplyStackedPosition(anchor ?? ToastAnchor.BottomRight);
					}
				}
			};

			if (owner.IsVisible) {
				toast.Show(owner);
			} else {
				toast.Show();
			}

			if (!toast.IsVisible) return;
			await Task.Delay(duration.Value + TimeSpan.FromMilliseconds(SlideDurationMs * 2));
			await toast.DismissAsync();
		});
	}

	private async Task DismissAsync()
	{
		if (!IsVisible) return;

		var deltaX = GetSlideDistance();
		await CreateSlideAnimation(deltaX, SlideDurationMs / 2, slideIn: false).RunAsync(this);
		if (IsVisible) {
			Close();
		}
	}

	private static Animation CreateSlideAnimation(double deltaX, int durationMs, bool slideIn)
	{
		return new Animation {
			Duration = TimeSpan.FromMilliseconds(durationMs),
			Children = {
				new KeyFrame {
					Cue = new Cue(0d),
					Setters = {
						new Setter(TranslateTransform.XProperty, slideIn ? deltaX : 0d)
					}
				},
				new KeyFrame {
					Cue = new Cue(1d),
					Setters = {
						new Setter(TranslateTransform.XProperty, slideIn ? 0d : deltaX)
					}
				}
			}
		};
	}

	private double GetSlideDistance()
	{
		var width = Width > 0 ? Width : Bounds.Width;
		return width + DefaultMargin;
	}

	private void ApplyStackedPosition(ToastAnchor anchor)
	{
		var screen = Screens.ScreenFromVisual(this);
		if (screen == null) return;

		var workingArea = screen.WorkingArea;
		double width = Bounds.Width > 0 ? Bounds.Width : Width;
		double height = Bounds.Height > 0 ? Bounds.Height : 100;
		int right = workingArea.X + workingArea.Width;
		int bottom = workingArea.Y + workingArea.Height;

		int index;
		lock (activeToasts) {
			index = activeToasts.IndexOf(this);
		}

		int offsetY = index * ((int)height + StackSpacing);
		PixelPoint position;
		switch (anchor) {
			case ToastAnchor.BottomRight:
				position = new PixelPoint(
					right - (int)width - DefaultMargin,
					bottom - (int)height - DefaultMargin - offsetY
				);
				break;
			case ToastAnchor.TopRight:
				position = new PixelPoint(
					right - (int)width - DefaultMargin,
					workingArea.Y + DefaultMargin + offsetY
				);
				break;
			default:
				return;
		}
		Position = position;
	}
}