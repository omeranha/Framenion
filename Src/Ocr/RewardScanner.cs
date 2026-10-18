using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace framenion.Src.Ocr;

public sealed class ScannedReward
{
	public required string Name { get; init; }
	public required Rect TitleRect { get; init; }
	public required double Confidence { get; init; }
	public required MatchMode Mode { get; init; }
	public required bool Ignored { get; init; }
}

/// <summary>
/// Reads the relic reward selection screen: locates the reward card titles, binarizes each one
/// and matches the text against the item pool.
/// </summary>
public sealed class RewardScanner(IOcrEngine engine, IReadOnlyList<string> pool)
{
	// Names wrap to two lines in 3/4 player layouts, so each card title is read as overlapping
	// bands plus the whole strip.
	private static readonly (double Top, double Height)[] titleBands = [
		(0.0, 0.58),
		(0.42, 0.58),
		(0.0, 1.0),
		// Tighter split for a wrapped name. The pair above overlaps by 16% of the strip, which
		// slices through the first line's glyph bottoms and leaves a ragged remnant above the
		// second line; that remnant reads as garbage words and pollutes the joined candidate.
		(0.0, 0.52),
		(0.50, 0.50),
	];

	/// <summary>Band pairs whose reads are joined to reconstruct a name that wrapped.</summary>
	private static readonly (int First, int Second)[] wrapPairs = [(0, 1), (3, 4)];

	/// <summary>Directory to dump the binarized strips into; null disables the dump.</summary>
	public string? DebugDirectory { get; init; }

	/// <summary>
	/// Reads every candidate card layout and keeps the one whose slots matched best. The number
	/// of reward cards is not known up front, and a layout probed against the wrong card count
	/// lands its slots in the gaps between cards, where nothing matches.
	/// </summary>
	public async Task<IReadOnlyList<ScannedReward>> ScanAsync(Mat frame, double uiScale)
		=> await ScanAsync(frame, uiScale, null);

	public async Task<IReadOnlyList<ScannedReward>> ScanAsync(Mat frame, double uiScale, ScanRegion? band)
	{
		if (frame.Empty() || pool.Count == 0) return [];

		var layouts = RewardLayout.CandidateLayouts(frame.Width, frame.Height, uiScale, band);
		var slotCache = new Dictionary<Rect, RewardMatch?>();

		IReadOnlyList<ScannedReward> best = [];
		double bestScore = 0;

		foreach (var layout in layouts) {
			List<ScannedReward> rewards = [];
			double score = 0;

			foreach (var titleRect in layout.TitleRects) {
				if (!slotCache.TryGetValue(titleRect, out var match)) {
					match = await ReadSlotAsync(frame, titleRect);
					slotCache[titleRect] = match;
				}

				if (match == null) continue;

				score += match.Value.Confidence + 0.6 + (match.Value.Mode == MatchMode.Exact ? 0.35 : 0);
				rewards.Add(new ScannedReward {
					Name = match.Value.Name,
					TitleRect = titleRect,
					Confidence = match.Value.Confidence,
					Mode = match.Value.Mode,
					Ignored = RewardPool.IsIgnored(match.Value.Name),
				});
			}

			if (score > bestScore) {
				bestScore = score;
				best = rewards;
			}
		}

		Log.Info($"[Rewards] Best layout: {best.Count} reward(s) (score {bestScore:0.00}) from {layouts.Count} candidate layout(s), pool of {pool.Count}.");
		return best;
	}

	private async Task<RewardMatch?> ReadSlotAsync(Mat frame, Rect titleRect)
	{
		using var crop = frame[titleRect].Clone();

		var reads = new List<string>(titleBands.Length);
		for (int i = 0; i < titleBands.Length; i++) {
			var (top, height) = titleBands[i];
			using var strip = OcrPreprocess.BinarizeRegion(crop, top, height);
			if (strip == null) {
				reads.Add(string.Empty);
				continue;
			}

			if (DebugDirectory != null) {
				Cv2.ImWrite(Path.Combine(DebugDirectory, $"debug_slot_{titleRect.X}_{i}.png"), strip);
			}

			var lines = await engine.RecognizeAsync(strip);
			reads.Add(RewardMatcher.CleanOcrText(string.Join(' ', lines.Select(l => l.Text))));
		}

		// The two half-bands isolate one text line each, so joining them reconstructs a wrapped
		// name that neither band holds on its own.
		var candidates = new List<string>(wrapPairs.Length + titleBands.Length);
		foreach (var (first, second) in wrapPairs) {
			string joined = string.Join(' ', new[] { reads[first], reads[second] }.Where(r => r.Length > 0));
			if (joined.Length > 0) candidates.Add(joined);
		}
		candidates.AddRange(reads.Where(r => r.Length > 0));

		RewardMatch? best = null;
		string bestFrom = "";
		foreach (string candidate in candidates.Distinct(StringComparer.Ordinal)) {
			var match = RewardMatcher.Match(candidate, pool);
			if (match == null) {
				Log.Debug($"[Rewards] slot x={titleRect.X}: \"{candidate}\" matched nothing.");
				continue;
			}

			bool confident = RewardMatcher.IsConfident(match.Value);
			Log.Debug($"[Rewards] slot x={titleRect.X}: \"{candidate}\" -> \"{match.Value.Name}\""
				+ $" ({match.Value.Mode}, {match.Value.Confidence:0.000}{(confident ? "" : ", rejected")})");
			if (!confident) continue;

			if (best == null || match.Value.Confidence > best.Value.Confidence) {
				best = match;
				bestFrom = candidate;
			}
		}

		if (best != null) {
			Log.Info($"[Rewards] slot x={titleRect.X}: \"{best.Value.Name}\" from \"{bestFrom}\" ({best.Value.Mode}, {best.Value.Confidence:0.000}).");
		}

		return best;
	}
}
