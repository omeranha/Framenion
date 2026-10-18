using framenion.Src.Ocr;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace framenion.Src.Riven;

public enum RivenScanLayout
{
	/// <summary>The centered card on the Kuva reroll screen.</summary>
	Reroll,

	/// <summary>A riven linked in chat, which sits higher on screen.</summary>
	Chat,

	/// <summary>The right-hand card during the post-roll transition.</summary>
	Roll,
}

/// <summary>
/// Reads a riven card off the screen. The Windows OCR engine returns located text lines in a
/// single pass, so the card needs no separate text detector ahead of recognition.
/// </summary>
public sealed class RivenScanner(IOcrEngine engine)
{
	/// <summary>At most two OCR passes per frame: the geometric band, then the text-bounds crop.</summary>
	private const int maxPassesPerFrame = 2;

	private const int minAcceptableStats = 2;

	/// <summary>A card carries at most three buffs and one curse.</summary>
	private const int maxStats = 4;

	public string? DebugDirectory { get; init; }

	public async Task<RivenScanResult?> ScanAsync(Mat frame, RivenScanLayout layout)
	{
		// anchorRect is where the card is, which is not always the crop the stats are read from:
		// the post-roll crop is a tighter read window on the very same card the reroll layout
		// sees, so anchoring to it moved the overlay between the two scans of one riven.
		var (cardRect, band, anchorRect) = layout switch {
			RivenScanLayout.Chat => (RivenScanImage.ChatCard, RivenScanImage.ChatBand, RivenScanImage.ChatCard),
			RivenScanLayout.Roll => (RivenScanImage.RollCard, RivenScanImage.RollBand, RivenScanImage.SingleCard),
			_ => (RivenScanImage.SingleCard, RivenScanImage.RerollBand, RivenScanImage.SingleCard),
		};

		var regions = AppData.AppSettings.Regions;
		var statOverride = layout == RivenScanLayout.Chat ? regions.RivenChat : regions.RivenReroll;

		// A user-defined stat region replaces the derived card crop and band entirely.
		if (statOverride is { } area) {
			var rect = new RivenRect(area.X, area.Y, area.Width, area.Height);
			var bounds = RivenScanImage.ToPixels(frame, rect);
			using var statArea = RivenScanImage.CropRatio(frame, rect);
			Dump(statArea, "card");

			var direct = await ReadStatAreaAsync(statArea, 0);
			// The region is the stat block; the overlay anchors to the card around it.
			if (direct != null) direct.Card = ToBounds(RivenScanImage.CardFromStatArea(bounds, band));
			return await FinishAsync(frame, layout, direct);
		}

		using var card = RivenScanImage.CropRatio(frame, cardRect);
		Dump(card, "card");

		RivenScanResult? best = null;

		for (int pass = 0; pass < maxPassesPerFrame; pass++) {
			using var statArea = pass == 0
				? RivenScanImage.CropStatArea(card, band)
				: RivenScanImage.CropStatAreaFallback(card);
			if (statArea == null || statArea.Empty()) continue;

			var result = await ReadStatAreaAsync(statArea, pass);
			if (result == null) continue;

			if (best == null || Prefer(result, best)) best = result;

			// Only a full four-stat read proves nothing was clipped: a riven legitimately has
			// two or three lines, so a short count cannot be told from a cut-off one. Anything
			// less and the text-bounds framing gets a turn as well.
			if (result.Stats.Count >= maxStats && result.Stats.All(s => s.Value != null)) break;
		}

		if (best != null) best.Card = CardAnchor(frame, anchorRect);
		return await FinishAsync(frame, layout, best);
	}

	/// <summary>
	/// The card's rect in frame pixels. Deliberately geometric: an anchor that tracked the
	/// detected text landed somewhere different on every roll even though the card never moves.
	/// </summary>
	private static PixelBounds CardAnchor(Mat frame, RivenRect anchorRect)
	{
		var bounds = RivenScanImage.ToPixels(frame, anchorRect);
		var column = RivenScanImage.CardColumn(bounds.Width, bounds.Height);
		return new PixelBounds(bounds.X + column.X, bounds.Y + column.Y, column.Width, column.Height);
	}

	private static PixelBounds ToBounds(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

	private async Task<RivenScanResult?> FinishAsync(Mat frame, RivenScanLayout layout, RivenScanResult? best)
	{
		if (best == null || best.Stats.Count < minAcceptableStats) {
			Log.Info($"[Riven] Read {best?.Stats.Count ?? 0} stat(s); below the minimum of {minAcceptableStats}.");
			return best;
		}

		best.WeaponName = await ReadWeaponNameAsync(frame, layout) ?? RivenData.FindWeaponInText(best.RawText) ?? "";
		if (best.WeaponName.Length == 0) {
			Log.Warn("[Riven] Could not identify the weapon; stats were read but cannot be graded.");
			return best;
		}

		best.Grade = RivenGrading.Grade(best.WeaponName, best.Stats);
		return best;
	}

	/// <summary>Prefers a complete read, then more stats, then fewer missing values.</summary>
	private static bool Prefer(RivenScanResult candidate, RivenScanResult current)
	{
		if (current.Incomplete && !candidate.Incomplete) return true;
		if (!current.Incomplete && candidate.Incomplete) return false;
		if (candidate.Stats.Count != current.Stats.Count) return candidate.Stats.Count > current.Stats.Count;
		return candidate.Stats.Count(s => s.Value == null) < current.Stats.Count(s => s.Value == null);
	}

	private async Task<RivenScanResult?> ReadStatAreaAsync(Mat statArea, int pass)
	{
		using var upscaled = RivenScanImage.Upscale(statArea);

		RivenScanResult? best = null;

		// Two preprocessings. Otsu is sharper when it works, but how much card background falls
		// inside the crop moves its threshold enough to lose the text entirely, so the plain
		// contrast-stretched grayscale is tried alongside it.
		foreach (var variant in (string[])["plain", "otsu"]) {
			using var prepared = variant == "otsu"
				? OcrPreprocess.BinarizeRegion(upscaled, 0, 1)
				: OcrPreprocess.EnhanceRegion(upscaled, 0, 1);
			if (prepared == null) continue;

			Dump(prepared, $"stats{pass}-{variant}");

			var lines = await engine.RecognizeAsync(prepared);
			if (lines.Count == 0) {
				Log.Debug($"[Riven] pass {pass} / {variant}: no text");
				continue;
			}

			// Card order drives both the curse position and the combined-element inheritance.
			var text = string.Join('\n', lines
				.OrderBy(l => l.Box.Y)
				.Select(l => l.Text)
				.Where(t => !string.IsNullOrWhiteSpace(t)));

			var stats = RivenTextParser.ParseStats(text);
			Log.Debug($"[Riven] pass {pass} / {variant}: {stats.Count} stat(s) from \"{text.Replace('\n', '|')}\"");

			var result = new RivenScanResult {
				Stats = stats,
				RawText = text,
				Incomplete = RivenTextParser.IsIncompleteRead(stats),
			};

			if (best == null || Prefer(result, best)) best = result;
		}

		return best;
	}

	/// <summary>
	/// Reads the weapon off the "FITS IN" plate. Tries the tight name crop, then its inverse,
	/// then the wider panel, since a bright shard behind the plate can swallow the caption.
	/// </summary>
	private async Task<string?> ReadWeaponNameAsync(Mat frame, RivenScanLayout layout)
	{
		var regions = AppData.AppSettings.Regions;
		var configured = layout == RivenScanLayout.Chat ? regions.RivenChatWeapon : regions.RivenRerollWeapon;

		// A configured plate is authoritative; otherwise fall back to the measured crops.
		IReadOnlyList<(RivenRect Rect, bool Invert)> attempts = configured is { } area
			? [(new RivenRect(area.X, area.Y, area.Width, area.Height), false),
			   (new RivenRect(area.X, area.Y, area.Width, area.Height), true)]
			: layout == RivenScanLayout.Chat
				// A chat link has no "FITS IN" plate; the weapon is the card's own title.
				? [(RivenScanImage.ChatTitle, false),
				   (RivenScanImage.ChatTitle, true),
				   (RivenScanImage.ChatCard, false)]
				: [(RivenScanImage.FitsInName, false),
				   (RivenScanImage.FitsInName, true),
				   (RivenScanImage.FitsInPanel, false),
				   (RivenScanImage.FitsInWide, false)];

		foreach (var (rect, invert) in attempts) {
			using var crop = RivenScanImage.CropRatio(frame, rect);
			if (crop.Empty()) continue;

			using var scaled = RivenScanImage.Upscale(crop);
			using var source = invert ? Invert(scaled) : scaled.Clone();

			// Same two preprocessings as the stat block: a bright shard behind the plate can
			// swallow the caption once it is thresholded.
			foreach (var variant in (string[])["plain", "otsu"]) {
				using var prepared = variant == "otsu"
					? OcrPreprocess.BinarizeRegion(source, 0, 1)
					: OcrPreprocess.EnhanceRegion(source, 0, 1);
				if (prepared == null) continue;

				var lines = await engine.RecognizeAsync(prepared);
				if (lines.Count == 0) continue;

				var texts = lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
				Dump(prepared, $"fitsin{(invert ? "-inv" : "")}-{variant}");
				Log.Debug($"[Riven] weapon plate ({variant}{(invert ? ", inverted" : "")}): {string.Join(" / ", texts)}");

				// Skip the plate's own heading before matching.
				var candidates = texts.Where(t => !IsHeading(t)).ToList();
				var match = RivenData.FindWeaponByLabel(candidates);
				if (match != null) {
					Log.Info($"[Riven] Weapon label: \"{match.Value.Name}\" ({(match.Value.Exact ? "exact" : "fuzzy")}).");
					return match.Value.Name;
				}
			}
		}

		return null;
	}

	private static bool IsHeading(string text)
	{
		var squashed = new string(text.ToLowerInvariant().Where(char.IsAsciiLetter).ToArray());
		return squashed.Contains("tsin", StringComparison.Ordinal) || squashed.Contains("fitsi", StringComparison.Ordinal);
	}

	private static Mat Invert(Mat source)
	{
		var inverted = new Mat();
		Cv2.BitwiseNot(source, inverted);
		return inverted;
	}

	private void Dump(Mat image, string label)
	{
		if (DebugDirectory == null || image.Empty()) return;
		try {
			Cv2.ImWrite(Path.Combine(DebugDirectory, $"debug_riven_{label}.png"), image);
		} catch (Exception ex) {
			Log.Debug($"Could not write riven debug dump: {ex.Message}");
		}
	}
}
