using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace framenion.Src.Ocr;

public enum MatchMode
{
	None,
	Exact,
	Substring,
	Fuzzy,
}

public readonly record struct RewardMatch(string Name, double Confidence, MatchMode Mode);

/// <summary>
/// Maps a raw OCR read of a reward card title onto a known item name. Warframe's font makes the
/// readers trade characters for digit lookalikes, so a plain Levenshtein pass is not enough:
/// token aliases fix the recurring part-name misreads and confusion folding recovers the rest.
/// </summary>
public static class RewardMatcher
{
	private const double foldedExactConfidence = 0.99;
	private const double foldedNearSimilarity = 0.94;
	private const int foldedNearMinLength = 12;
	private const double substringScoreFloor = 0.88;
	private const double wrappedNameConfidence = 0.95;
	private const double minFuzzyNameLengthRatio = 0.45;
	private const double minFuzzyWordRatio = 0.45;
	private const double fuzzyFloor = 0.80;

	/// <summary>
	/// Trailing words a card title drops onto its second line. A band that holds only the first
	/// line therefore reads the whole name minus one of these.
	/// </summary>
	private static readonly string[] wrappedTailWords = ["blueprint"];

	private static readonly Dictionary<string, string> tokenAliases = new(StringComparer.Ordinal) {
		// Blueprint
		["bluedrint"] = "blueprint",
		["blueorint"] = "blueprint",
		["blueprlnt"] = "blueprint",
		["blueprini"] = "blueprint",
		["bluepnnt"] = "blueprint",
		["biueprint"] = "blueprint",
		["lueprint"] = "blueprint",
		// Systems
		["svst"] = "systems",
		["svstems"] = "systems",
		["systerns"] = "systems",
		["syst"] = "systems",
		["syslems"] = "systems",
		// Neuroptics
		["neurootics"] = "neuroptics",
		["neurotics"] = "neuroptics",
		["neuroptlcs"] = "neuroptics",
		["neurcptics"] = "neuroptics",
		["neuropiics"] = "neuroptics",
		["neuraptics"] = "neuroptics",
		["eurobtic"] = "neuroptics",
		["europtics"] = "neuroptics",
		// Chassis
		["chassls"] = "chassis",
		["chassi"] = "chassis",
		["chassl"] = "chassis",
		["hassis"] = "chassis",
		// Receiver
		["recelver"] = "receiver",
		["recelvar"] = "receiver",
		["recei"] = "receiver",
		// Common Warframe name misreads
		["wukon"] = "wukong",
		["rhln"] = "rhino",
		["rhin"] = "rhino",
		["sarv"] = "saryn",
		["nek"] = "nekros",
		["nekr"] = "nekros",
		["obero"] = "oberon",
		["obenon"] = "oberon",
		["trinlty"] = "trinity",
	};

	private static readonly ConcurrentDictionary<string, string> normalizedCache = new(StringComparer.Ordinal);
	private static readonly ConcurrentDictionary<string, string> foldedCache = new(StringComparer.Ordinal);

	public static RewardMatch? Match(string ocrText, IReadOnlyList<string> pool)
	{
		string read = NormalizeRewardText(ocrText);
		if (read.Length < 3 || pool.Count == 0) return null;

		string foldedRead = FoldOcrConfusions(read);
		var readWords = read.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		RewardMatch? best = null;
		foreach (string name in pool) {
			if (string.IsNullOrWhiteSpace(name)) continue;

			var candidate = Score(read, foldedRead, readWords, name);
			if (candidate == null) continue;
			if (best == null || candidate.Value.Confidence > best.Value.Confidence) {
				best = candidate;
				if (candidate.Value.Confidence >= 1.0) break;
			}
		}

		return best;
	}

	private static RewardMatch? Score(string read, string foldedRead, string[] readWords, string name)
	{
		string normalized = NormalizedName(name);
		if (normalized.Length == 0) return null;

		if (string.Equals(read, normalized, StringComparison.Ordinal)) {
			return new RewardMatch(name, 1.0, MatchMode.Exact);
		}

		string folded = FoldedName(name);
		if (string.Equals(foldedRead, folded, StringComparison.Ordinal)) {
			return new RewardMatch(name, foldedExactConfidence, MatchMode.Exact);
		}

		// A wrapped title puts "Blueprint" on its own line, so the band holding the first line
		// reads the name minus exactly that word. Containment alone cannot carry this: the
		// missing word costs enough similarity ("Equinox Prime Chassis" against its own name
		// scores 0.68) that the substring floor lands under the acceptance gate and a perfectly
		// good read is thrown away. Requiring the remainder to be precisely a known tail word
		// keeps it unambiguous.
		if (IsWrappedPrefix(read, normalized) || IsWrappedPrefix(foldedRead, folded)) {
			return new RewardMatch(name, wrappedNameConfidence, MatchMode.Substring);
		}

		double lengthRatio = Math.Min(read.Length, normalized.Length) / (double)Math.Max(read.Length, normalized.Length);
		if (read.Contains(normalized, StringComparison.Ordinal) || normalized.Contains(read, StringComparison.Ordinal)) {
			if (lengthRatio >= minFuzzyNameLengthRatio) {
				double similarity = Similarity(read, normalized);
				double confidence = Math.Max(similarity, substringScoreFloor);
				return new RewardMatch(name, confidence, MatchMode.Substring);
			}
		}

		// Folding collapses spacing too, so a read whose "B" was dropped and welded onto the
		// previous word still lines up with the pool name.
		if (folded.Length >= foldedNearMinLength) {
			double foldedSimilarity = Similarity(foldedRead, folded);
			if (foldedSimilarity >= foldedNearSimilarity) {
				return new RewardMatch(name, foldedSimilarity, MatchMode.Fuzzy);
			}
		}

		if (lengthRatio < minFuzzyNameLengthRatio) return null;

		var nameWords = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (WordMatchRatio(readWords, nameWords) < minFuzzyWordRatio) return null;

		double score = Similarity(read, normalized);
		return score >= fuzzyFloor ? new RewardMatch(name, score, MatchMode.Fuzzy) : null;
	}

	/// <summary>
	/// True when <paramref name="read"/> is <paramref name="name"/> with exactly one known
	/// trailing word missing, which is what a title that wrapped mid-name looks like. Works on
	/// both the spaced and the folded forms, hence the space-insensitive comparison.
	/// </summary>
	private static bool IsWrappedPrefix(string read, string name)
	{
		if (read.Length < 6 || name.Length <= read.Length) return false;
		if (!name.StartsWith(read, StringComparison.Ordinal)) return false;

		var remainder = name.AsSpan(read.Length).Trim();
		foreach (string tail in wrappedTailWords) {
			if (remainder.SequenceEqual(tail)) return true;
		}
		return false;
	}

	/// <summary>Fraction of the pool name's words that the read also contains, allowing misreads.</summary>
	private static double WordMatchRatio(string[] readWords, string[] nameWords)
	{
		if (nameWords.Length == 0) return 0;

		int matched = 0;
		foreach (string nameWord in nameWords) {
			foreach (string readWord in readWords) {
				if (WordsEquivalent(readWord, nameWord)) {
					matched++;
					break;
				}
			}
		}
		return matched / (double)nameWords.Length;
	}

	private static bool WordsEquivalent(string a, string b)
	{
		if (string.Equals(a, b, StringComparison.Ordinal)) return true;
		double threshold = Math.Max(a.Length, b.Length) >= 7 ? 0.70 : 0.78;
		return Similarity(a, b) >= threshold;
	}

	public static double Similarity(string a, string b)
	{
		if (a.Length == 0 || b.Length == 0) return 0;
		if (string.Equals(a, b, StringComparison.Ordinal)) return 1;
		int distance = LevenshteinDistance(a, b);
		return Math.Max(0, 1.0 - (distance / (double)Math.Max(a.Length, b.Length)));
	}

	private static int LevenshteinDistance(string a, string b)
	{
		var previous = new int[b.Length + 1];
		var current = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++) previous[j] = j;

		for (int i = 1; i <= a.Length; i++) {
			current[0] = i;
			for (int j = 1; j <= b.Length; j++) {
				int cost = a[i - 1] == b[j - 1] ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
			}
			(previous, current) = (current, previous);
		}

		return previous[b.Length];
	}

	private static string NormalizedName(string name) => normalizedCache.GetOrAdd(name, static n => NormalizeRewardText(n));

	private static string FoldedName(string name) => foldedCache.GetOrAdd(name, static n => FoldOcrConfusions(NormalizedName(n)));

	/// <summary>
	/// Normalizes a read or a pool name to lowercase alphanumeric words, applying the known
	/// per-token misread aliases.
	/// </summary>
	public static string NormalizeRewardText(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return string.Empty;

		var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.Select(NormalizeRewardWord)
			.Where(w => w.Length > 0);
		string joined = string.Join(' ', words);

		// The card prints a bonus count as "2 X" while the pool name is "2X Forma Blueprint",
		// so collapse the spacing before anything compares the two.
		int space = joined.IndexOf(' ');
		if (space > 0 && joined.AsSpan(space + 1).StartsWith("x") && joined.AsSpan(0, space).ToString().All(char.IsAsciiDigit)) {
			bool wholeToken = joined.Length == space + 2 || joined[space + 2] == ' ';
			if (wholeToken) {
				joined = string.Concat(joined.AsSpan(0, space), joined.AsSpan(space + 1));
			}
		}

		return joined;
	}

	private static string NormalizeRewardWord(string word)
	{
		string normalized = StripToAlphanumeric(NormalizeForSearch(word));
		if (normalized.Length == 0) return string.Empty;
		return tokenAliases.TryGetValue(normalized, out var alias) ? alias : normalized;
	}

	/// <summary>
	/// High resolution captures print digit lookalikes: "Blueprint" comes back as "81ueprint".
	/// Folding those back and dropping spacing recovers the name.
	/// </summary>
	public static string FoldOcrConfusions(string value)
	{
		if (string.IsNullOrEmpty(value)) return string.Empty;

		var builder = new StringBuilder(value.Length);
		foreach (char c in value.ToLowerInvariant()) {
			char folded = c switch {
				'8' => 'b',
				'0' => 'o',
				'1' => 'l',
				'5' => 's',
				_ => c,
			};
			if (char.IsAsciiLetterOrDigit(folded)) {
				builder.Append(folded);
			}
		}
		return builder.ToString();
	}

	private static string NormalizeForSearch(string value)
	{
		string decomposed = value.Normalize(NormalizationForm.FormD);
		var builder = new StringBuilder(decomposed.Length);
		foreach (char c in decomposed) {
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
			builder.Append(char.ToLowerInvariant(c));
		}
		return builder.ToString();
	}

	private static string StripToAlphanumeric(string value)
	{
		var builder = new StringBuilder(value.Length);
		foreach (char c in value) {
			if (char.IsAsciiLetterOrDigit(c)) {
				builder.Append(c);
			}
		}
		return builder.ToString();
	}

	/// <summary>Drops single-character OCR noise but keeps "&amp;" for names like "Cobra &amp; Crane Prime".</summary>
	public static string CleanOcrText(string text)
	{
		if (string.IsNullOrWhiteSpace(text)) return string.Empty;

		var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.Where(w => w == "&" || StripToAlphanumeric(w).Length > 1);
		return string.Join(' ', words);
	}

	/// <summary>Accepts a slot read only when the match is strong enough to overlay.</summary>
	public static bool IsConfident(RewardMatch match)
	{
		var words = match.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (words.Length <= 1 && match.Name.Length < 5) {
			return match.Mode == MatchMode.Exact && match.Confidence >= 0.99;
		}

		return match.Mode switch {
			MatchMode.Exact => match.Confidence >= 0.98,
			MatchMode.Substring => match.Confidence >= 0.92,
			_ => match.Confidence >= 0.86,
		};
	}
}
