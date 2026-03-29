using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace framenion;

public enum ToastAnchor
{
	BottomRightOfScreen,
	TopRightOfScreen,
	BottomRightOfOwnerWindow,
	TopRightOfOwnerWindow
}

public partial class ToastWindow : Window
{
	private static readonly object gate = new();
	private static readonly List<ToastWindow> active = [];
	private static int defaultMargin = 12;
	private static int defaultSpacing = 10;
	private ToastAnchor anchor = ToastAnchor.BottomRightOfScreen;

	private static readonly Animation FadeIn = new() {
		Duration = TimeSpan.FromMilliseconds(140),
		Children = {
			new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 0d) } },
			new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 1d) } },
		}
	};

	private static readonly TimeSpan SlideOutDuration = TimeSpan.FromMilliseconds(220);

	public ToastWindow()
	{
		InitializeComponent();
		RenderTransform ??= new TranslateTransform();
		RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative);
		PointerPressed += async (_, _) => await DismissAsync(reposition: true);
		Closed += (_, _) => OnToastClosed(this);
	}

	public static void ShowToast(Window owner, string title, string body, TimeSpan? duration = null, ToastAnchor anchor = ToastAnchor.BottomRightOfScreen,
		int margin = 12,
		int spacing = 10)
	{
		duration ??= TimeSpan.FromSeconds(6);
		defaultMargin = margin;
		defaultSpacing = spacing;

		var toastOwner = NormalizeToastOwner(owner, anchor);
		var toast = new ToastWindow { Topmost = true };
		toast.Owner = owner;
		toast.TitleText.Text = title;
		toast.BodyText.Text = body;
		toast.anchor = anchor;

		try {
			toast.ShowActivated = false;
		} catch { }

		lock (gate) {
			active.Add(toast);
		}

		toast.Opened += async (_, _) => {
			RepositionToasts(toastOwner, anchor, margin, spacing);
			await FadeIn.RunAsync(toast, default);
		};

		toast.Show(toastOwner);
		_ = AutoCloseAsync(toastOwner, toast, duration.Value);
	}

	private static async Task AutoCloseAsync(Window owner, ToastWindow toast, TimeSpan duration)
	{
		await Task.Delay(duration);
		if (!toast.IsVisible) return;

		await toast.DismissAsync(reposition: true);
	}

	private async Task DismissAsync(bool reposition)
	{
		if (!IsVisible)
			return;

		// Slide the *content* right, then close the window.
		double deltaX = (Width > 0 ? Width : Bounds.Width);
		if (deltaX <= 0) {
			deltaX = 360;
		}
		deltaX += defaultMargin;

		var slideOutRight = CreateSlideOutRight(deltaX);

		await slideOutRight.RunAsync(this, default);

		if (IsVisible) {
			Close();
		}

		if (reposition && Owner is Window owner) {
			RepositionToasts(owner, ToastAnchor.BottomRightOfScreen, defaultMargin, defaultSpacing);
		}
	}

	private static Animation CreateSlideOutRight(double deltaX) => new() {
		Duration = SlideOutDuration,
		Children = {
			new KeyFrame {
				Cue = new Cue(0d),
				Setters = { new Setter(TranslateTransform.XProperty, 0d) }
			},
			new KeyFrame {
				Cue = new Cue(1d),
				Setters = { new Setter(TranslateTransform.XProperty, deltaX) }
			},
		}
	};

	private static void OnToastClosed(ToastWindow toast)
	{
		lock (gate) {
			active.Remove(toast);
		}
	}

	private static void RepositionToasts(Window owner, ToastAnchor anchor, int margin, int spacing)
	{
		ToastWindow[] toasts;
		lock (gate) {
			toasts = [.. active.Where(t => t.IsVisible)];
		}

		if (toasts.Length == 0) return;

		PixelRect area;
		if (anchor is ToastAnchor.BottomRightOfOwnerWindow or ToastAnchor.TopRightOfOwnerWindow) {
			var ownerPos = owner.Position;
			var ownerSize = owner.Bounds.Size;

			var scale = owner.RenderScaling;
			var w = (int)Math.Ceiling(ownerSize.Width * scale);
			var h = (int)Math.Ceiling(ownerSize.Height * scale);
			area = new PixelRect(ownerPos, new PixelSize(w, h));
		} else {
			var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary;
			if (screen is null) return;

			area = screen.WorkingArea;
		}

		bool stackDown = anchor is ToastAnchor.TopRightOfScreen or ToastAnchor.TopRightOfOwnerWindow;
		for (int i = 0; i < toasts.Length; i++) {
			var toast = toasts[i];
			int toastW = (int)Math.Ceiling(toast.Width);
			int toastH = (int)Math.Ceiling(toast.Height);
			int x = area.X + area.Width - toastW - margin;
			// top-right, stack downward or bottom-right, stack upward
			int y = stackDown ? area.Y + margin + i * (toastH + spacing) : area.Y + area.Height - margin - toastH - i * (toastH + spacing);
			toast.Position = new PixelPoint(x, y);
		}
	}

	private static Window NormalizeToastOwner(Window owner, ToastAnchor anchor)
	{
		if (anchor is not (ToastAnchor.BottomRightOfOwnerWindow or ToastAnchor.TopRightOfOwnerWindow)) return owner;

		Window? current = owner;
		while (current is not null) {
			if (current is MainWindow main) return main;

			current = current.Owner as Window;
		}

		current = owner;
		Window root = owner;
		while (current?.Owner is Window parent) {
			root = parent;
			current = parent;
		}

		return root;
	}
}