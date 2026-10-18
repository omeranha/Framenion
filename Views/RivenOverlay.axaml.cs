using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using framenion.Src;
using framenion.Src.Ocr;
using framenion.Src.Riven;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace framenion;

public partial class RivenOverlayWindow : Window
{
	/// <summary>Kept open across scans so a reroll updates in place instead of flashing a new window.</summary>
	private static RivenOverlayWindow? current;

	private static readonly IBrush gradeS = new SolidColorBrush(Color.Parse("#FFD700"));
	private static readonly IBrush gradeA = new SolidColorBrush(Color.Parse("#7FD858"));
	private static readonly IBrush gradeB = new SolidColorBrush(Color.Parse("#D0D0D0"));
	private static readonly IBrush gradeC = new SolidColorBrush(Color.Parse("#E08A3C"));
	private static readonly IBrush gradeF = new SolidColorBrush(Color.Parse("#D9544F"));

	public static readonly IValueConverter GradeBrushConverter =
		new FuncValueConverter<string?, IBrush>(grade => BrushForGrade(grade ?? ""));

	public string WeaponName { get; private set; } = "";
	public string OverallGrade { get; private set; } = "";
	public string Subtitle { get; private set; } = "";
	public string Warning { get; private set; } = "";
	public bool HasGrade { get; private set; }
	public bool HasWarning => Warning.Length > 0;
	public IBrush OverallGradeBrush { get; private set; } = gradeB;
	public List<GradedRivenStat> Stats { get; private set; } = [];

	/// <summary>Last anchor, replayed when layout changes the window's size.</summary>
	private PixelBounds anchorCard;
	private int anchorX;
	private int anchorY;

	public RivenOverlayWindow()
	{
		InitializeComponent();
		DataContext = this;
	}

	private static IBrush BrushForGrade(string grade) => grade switch {
		"S" or "A+" => gradeS,
		"A" or "A-" => gradeA,
		"B+" or "B" or "B-" => gradeB,
		"C+" or "C" or "C-" => gradeC,
		"F" => gradeF,
		_ => gradeB,
	};

	public static void Display(RivenScanResult result, int frameOriginX, int frameOriginY)
	{
		Dispatcher.UIThread.Post(() => {
			var owner = AppData.MainWindow;
			if (owner == null) return;

			current ??= Create(owner);
			current.Apply(result);
			current.Reposition(result.Card, frameOriginX, frameOriginY);

			if (!current.IsVisible) {
				if (owner.IsVisible) current.Show(owner);
				else current.Show();
			}
		});
	}

	public static void HideOverlay()
	{
		Dispatcher.UIThread.Post(() => {
			if (current is { IsVisible: true }) current.Hide();
		});
	}

	/// <summary>
	/// The overlay has to accept clicks for this button to work, which means clicking it takes
	/// focus off the game, so hand focus straight back.
	/// </summary>
	private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		Hide();
		ScreenCapture.TryFocusGameWindow();
	}

	public static void CloseOverlay()
	{
		Dispatcher.UIThread.Post(() => {
			current?.Close();
			current = null;
		});
	}

	private static RivenOverlayWindow Create(Window owner)
	{
		var window = new RivenOverlayWindow {
			WindowStartupLocation = WindowStartupLocation.Manual,
		};
		try {
			window.ShowActivated = false;
		} catch (Exception ex) {
			Log.Debug($"Could not suppress activation for the riven overlay: {ex.Message}");
		}

		// SizeToContent means the real height only exists after layout, and the first
		// Reposition runs before the window has ever been shown, so re-anchor once sized.
		window.SizeChanged += (_, _) => window.Reposition(window.anchorCard, window.anchorX, window.anchorY);

		window.Closed += (_, _) => {
			if (ReferenceEquals(current, window)) current = null;
		};
		return window;
	}

	private void Apply(RivenScanResult result)
	{
		WeaponName = result.WeaponName.Length > 0 ? result.WeaponName : "Unknown weapon";
		Stats = result.Grade != null
			? [.. result.Grade.Stats]
			: [.. result.Stats.Select(s => new GradedRivenStat {
				Name = s.Name,
				Positive = s.Positive,
				DisplayPositive = s.DisplayPositive,
				Value = s.Value,
				Multiplier = s.Multiplier,
				Grade = "?",
				RollFloat = 0.5,
			})];

		HasGrade = result.Grade != null;
		if (result.Grade is { } grade) {
			OverallGrade = grade.OverallGrade;
			OverallGradeBrush = BrushForGrade(grade.OverallGrade);
			var type = grade.RivenTypeName.Length > 0 ? grade.RivenTypeName : "Riven";
			Subtitle = $"{type}  ·  dispo {grade.Disposition.ToString("0.00", CultureInfo.InvariantCulture)}"
				+ $"  ·  rank {grade.AssumedRank}  ·  {grade.StatPerfectness * 100:0}% roll";
		} else {
			OverallGrade = "";
			Subtitle = "";
		}

		Warning = result.Grade == null
			? result.WeaponName.Length == 0
				? "Weapon not identified, so the roll could not be graded."
				: "This weapon has no riven data, so the roll could not be graded."
			: result.Incomplete
				? "The read looks short; some stats may be missing."
				: "";

		// Plain CLR properties on a window: re-assigning the context refreshes every binding.
		DataContext = null;
		DataContext = this;
	}

	/// <summary>
	/// Parks the panel immediately right of the card that was read, falling back to its left
	/// when there is no room, and clamped to the screen the card sits on.
	/// </summary>
	private void Reposition(PixelBounds card, int frameOriginX, int frameOriginY)
	{
		const int margin = 14;

		anchorCard = card;
		anchorX = frameOriginX;
		anchorY = frameOriginY;

		int cardLeft = frameOriginX + card.X;
		int cardTop = frameOriginY + card.Y;

		// Resolve the screen from the card, not from this window: before the first Show() the
		// window has no meaningful position, which pinned the overlay to the primary monitor
		// whenever the game was running on another one.
		var screen = (card.IsEmpty
			? null
			: Screens.ScreenFromPoint(new PixelPoint(cardLeft + (card.Width / 2), cardTop + (card.Height / 2))))
			?? Screens.ScreenFromWindow(this)
			?? Screens.Primary;
		if (screen == null) return;

		var area = screen.WorkingArea;
		double scaling = screen.Scaling;

		// Bounds is the laid-out size and is only available once shown; Width is the declared one.
		double dipWidth = Bounds.Width > 0 ? Bounds.Width : Width > 0 ? Width : 340;
		double dipHeight = Bounds.Height > 0 ? Bounds.Height : 260;
		int width = (int)Math.Round(dipWidth * scaling);
		int height = (int)Math.Round(dipHeight * scaling);

		int x, y;
		if (card.IsEmpty) {
			// No card geometry (nothing was located): fall back to the right-hand side.
			x = area.X + (int)(area.Width * 0.78);
			y = area.Y + (int)(area.Height * 0.26);
		} else {
			x = cardLeft + card.Width + margin;
			// Not enough room on the right: sit on the other side of the card instead.
			if (x + width > area.X + area.Width) x = cardLeft - width - margin;
			y = cardTop;
		}

		x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - width));
		y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - height));
		Position = new PixelPoint(x, y);

		Log.Debug($"[Riven] Overlay at {x},{y} ({width}x{height} px @ {scaling:0.##}x) for card {cardLeft},{cardTop} {card.Width}x{card.Height}; screen {area.X},{area.Y} {area.Width}x{area.Height}.");
	}
}
