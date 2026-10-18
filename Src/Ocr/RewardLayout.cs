using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace framenion.Src.Ocr;

public readonly record struct RatioRect(double X, double Y, double Width, double Height);

public sealed class RewardSlotLayout
{
	public required int Count { get; init; }
	public required Rect[] TitleRects { get; init; }
}

/// <summary>
/// Reward card geometry, measured as fractions of the game's content area. The reward row is
/// centered, so the number of cards (one per squad member) only shifts the outer slots.
/// </summary>
public static class RewardLayout
{
	private static readonly Dictionary<int, RatioRect[]> fixedLayouts = new() {
		[1] = [new(0.439, 0.225, 0.122, 0.225)],
		[2] = [new(0.3755, 0.225, 0.122, 0.225), new(0.5025, 0.225, 0.122, 0.225)],
		[3] = [new(0.312, 0.225, 0.122, 0.225), new(0.439, 0.225, 0.122, 0.225), new(0.566, 0.225, 0.122, 0.225)],
		[4] = [new(0.245, 0.225, 0.122, 0.225), new(0.372, 0.225, 0.122, 0.225), new(0.499, 0.225, 0.122, 0.225), new(0.626, 0.225, 0.122, 0.225)],
	};

	// The ratios were measured on 16:9 at Warframe's default UI scale slider.
	private const double referenceAspect = 16.0 / 9.0;
	private const double referenceUiScale = 0.99;
	private const double minLayoutUiScale = 0.25;

	// The card title sits in the lower part of the card.
	private const double titleTopOfCard = 0.7;
	private const double titleHeightOfCard = 0.18;

	public static IReadOnlyList<RewardSlotLayout> CandidateLayouts(int frameWidth, int frameHeight, double uiScale)
		=> CandidateLayouts(frameWidth, frameHeight, uiScale, null);

	/// <summary>
	/// When <paramref name="band"/> is supplied, the measured row is remapped into it instead of
	/// being derived from the frame, so a user-defined region keeps the same slot proportions.
	/// </summary>
	public static IReadOnlyList<RewardSlotLayout> CandidateLayouts(int frameWidth, int frameHeight, double uiScale, ScanRegion? band)
	{
		var (scaleX, scaleY) = AspectScaleFor(frameWidth, frameHeight);
		var layouts = new List<RewardSlotLayout>(fixedLayouts.Count);

		// Horizontal extent of the four-card row, which the remap is relative to.
		var row = fixedLayouts[4];
		double rowLeft = row[0].X;
		double rowWidth = row[^1].X + row[^1].Width - rowLeft;

		foreach (var (count, cards) in fixedLayouts) {
			var rects = new Rect[cards.Length];
			for (int i = 0; i < cards.Length; i++) {
				RatioRect title;
				if (band is { } area) {
					// Keep each slot's position within the row, but inside the user's box.
					double start = (cards[i].X - rowLeft) / rowWidth;
					double span = cards[i].Width / rowWidth;
					title = new RatioRect(area.X + (start * area.Width), area.Y, span * area.Width, area.Height);
				} else {
					var card = AspectCorrect(UiScaleCorrect(cards[i], uiScale), scaleX, scaleY);
					title = new RatioRect(card.X, card.Y + (card.Height * titleTopOfCard), card.Width, card.Height * titleHeightOfCard);
				}

				rects[i] = ToPixels(title, frameWidth, frameHeight);
			}
			layouts.Add(new RewardSlotLayout { Count = count, TitleRects = rects });
		}

		return layouts;
	}

	/// <summary>
	/// Rescales the off axis about the center so the 16:9 ratios land correctly on wider
	/// (21:9) or taller (16:10, 4:3) frames.
	/// </summary>
	private static (double ScaleX, double ScaleY) AspectScaleFor(int width, int height)
	{
		if (width <= 0 || height <= 0) return (1, 1);
		double referenceWidth = height * referenceAspect;
		double referenceHeight = width / referenceAspect;
		return (referenceWidth < width ? referenceWidth / width : 1, referenceHeight < height ? referenceHeight / height : 1);
	}

	private static RatioRect AspectCorrect(RatioRect rect, double scaleX, double scaleY)
	{
		if (scaleX >= 1 && scaleY >= 1) return rect;
		return new RatioRect(0.5 + ((rect.X - 0.5) * scaleX), 0.5 + ((rect.Y - 0.5) * scaleY), rect.Width * scaleX, rect.Height * scaleY);
	}

	private static RatioRect UiScaleCorrect(RatioRect rect, double uiScale)
	{
		double scale = Math.Clamp(uiScale, minLayoutUiScale, 1.0) / referenceUiScale;
		return new RatioRect(0.5 + ((rect.X - 0.5) * scale), 0.5 + ((rect.Y - 0.5) * scale), rect.Width * scale, rect.Height * scale);
	}

	private static Rect ToPixels(RatioRect rect, int frameWidth, int frameHeight)
	{
		int left = Math.Clamp((int)Math.Round(rect.X * frameWidth), 0, Math.Max(0, frameWidth - 1));
		int top = Math.Clamp((int)Math.Round(rect.Y * frameHeight), 0, Math.Max(0, frameHeight - 1));
		int width = Math.Clamp((int)Math.Round(rect.Width * frameWidth), 1, frameWidth - left);
		int height = Math.Clamp((int)Math.Round(rect.Height * frameHeight), 1, frameHeight - top);
		return new Rect(left, top, width, height);
	}
}
