using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace framenion.Src.Riven;

/// <summary>
/// Repairs OCR output from a riven card and parses it into stats.
/// The order of the repair rules is load-bearing: comma-to-dot runs before the digit glue,
/// the digit lookalikes after the Combo Duration carve-out, and the sign re-lineariser last.
/// </summary>
public static class RivenTextParser
{
	private const double maxReasonableValue = 500;
	private const RegexOptions opts = RegexOptions.CultureInvariant;

	private static readonly (Regex Pattern, string Replacement)[] aliases = [
		(new Regex("Dannage", opts | RegexOptions.IgnoreCase), "Damage"),
		(new Regex("Darnage", opts | RegexOptions.IgnoreCase), "Damage"),
		(new Regex("Darnoge", opts | RegexOptions.IgnoreCase), "Damage"),
		(new Regex(@"\bDamage\s+to\s+Corpu\b", opts | RegexOptions.IgnoreCase), "Damage to Corpus"),
		(new Regex(@"\bDamage\s+to\s+Infeste\b", opts | RegexOptions.IgnoreCase), "Damage to Infested"),
		(new Regex(@"Crit\s*ical", opts | RegexOptions.IgnoreCase), "Critical"),
		(new Regex(@"Critica\b", opts | RegexOptions.IgnoreCase), "Critical"),
		(new Regex(@"Multi\s*shot", opts | RegexOptions.IgnoreCase), "Multishot"),
		(new Regex("Sta tus", opts | RegexOptions.IgnoreCase), "Status"),
		(new Regex("Statuc", opts | RegexOptions.IgnoreCase), "Status"),
		(new Regex("Re load", opts | RegexOptions.IgnoreCase), "Reload"),
		(new Regex("Elec tricity", opts | RegexOptions.IgnoreCase), "Electricity"),
		(new Regex("Punc ture", opts | RegexOptions.IgnoreCase), "Puncture"),
		(new Regex("Pi[uo]ncture", opts | RegexOptions.IgnoreCase), "Puncture"),
		(new Regex("Puincture", opts | RegexOptions.IgnoreCase), "Puncture"),
		(new Regex(@"\bReload\s+Spe[de]\b", opts | RegexOptions.IgnoreCase), "Reload Speed"),
		(new Regex("Maga zine", opts | RegexOptions.IgnoreCase), "Magazine"),
		(new Regex("Capaclty", opts | RegexOptions.IgnoreCase), "Capacity"),
		(new Regex("Maxinnunn", opts | RegexOptions.IgnoreCase), "Maximum"),
		(new Regex("Maximu[rn]", opts | RegexOptions.IgnoreCase), "Maximum"),
		(new Regex("Annnno", opts | RegexOptions.IgnoreCase), "Ammo"),
		(new Regex("Mel[ae]e", opts | RegexOptions.IgnoreCase), "Melee"),
		(new Regex("Fini sher", opts | RegexOptions.IgnoreCase), "Finisher"),
		(new Regex("Finlsher", opts | RegexOptions.IgnoreCase), "Finisher"),
		(new Regex("[>]?[lh]mpact", opts | RegexOptions.IgnoreCase), "Impact"),
		(new Regex(@"\bG[Ll]ash\b", opts), "Slash"),
		(new Regex(@"\bY\s*Puncture\b", opts), "Puncture"),
		(new Regex(@"\bA\s*Slash\b", opts), "Slash"),
		(new Regex(@"\bO\s*Cold\b", opts), "Cold"),
		(new Regex(@"\bO\s*Heat\b", opts), "Heat"),
		(new Regex(@"\bl\s*eat\b", opts), "Heat"),
		(new Regex(@"\bQ\s*Toxin\b", opts), "Toxin"),
		(new Regex(@"\bQ\s*Electricity\b", opts), "Electricity"),
		// The unique tail recovers "Additional" when the reader garbles or drops it.
		(new Regex(@"\b(?:[A-Za-z]{2,12}\s+)?Combo\s+Count\s+Chance\b", opts | RegexOptions.IgnoreCase), "Additional Combo Count Chance"),
	];

	// Two-word stat names the element icon splits across lines.
	private static readonly string[] rejoinHeads = [
		"Finisher", "Melee", "Critical", "Status", "Attack", "Reload", "Flight", "Projectile",
		"Fire", "Combo", "Additional", "Ammo", "Punch", "Channeling", "Weapon", "Heavy",
		"Initial", "Magazine", "Chance", "Damage", "Slide",
	];

	private static readonly Regex statEndWordLock = new(
		@"\b(" + string.Join('|', RivenStatTables.KnownStats
			.Select(s => s[(s.LastIndexOf(' ') + 1)..]).Distinct()) + @")[^\s%)]$",
		opts | RegexOptions.IgnoreCase | RegexOptions.Multiline);

	private static readonly Regex valueWithLookalike = new(
		@"([+\-\u2013]\s*|\bx\s*)((?:\d[\dgqlIOSB]*|[gqlIOSB](?=\.))(?:\.[\dgqlIOSB]+)?)(?=[\s%]|$)",
		opts | RegexOptions.Multiline);

	private const string numberEnd = @"(?!\.?\d|[A-Za-z](?![A-Za-z]))";
	private static readonly Regex xValue = new(@"x\s*(\d+\.?\d*)" + numberEnd, opts | RegexOptions.IgnoreCase);
	private static readonly Regex signedValue = new(@"[+\-\u2013]\s*(\d+\.?\d*)" + numberEnd, opts);
	private static readonly Regex percentValue = new(@"(\d+\.?\d*)\s*%", opts);
	private static readonly Regex signBeforeDigits = new(@"[+\-\u2013](?=\s*\d)", opts);
	private static readonly Regex valuePrefix = new(@"^[+\-\u2013x\u00d7]?[\d.,\s%]*", opts);
	private static readonly Regex valueOnlyLine = new(@"^[+\-\u2013x\d\s.,%]+$", opts | RegexOptions.IgnoreCase);
	private static readonly Regex bareInteger = new(@"^\d{1,4}$", opts);
	private static readonly Regex noisySign = new(@"[+\-\u2013]\s*\S", opts);

	/// <summary>Repairs a raw OCR read of the stat area.</summary>
	public static string Preprocess(string raw)
	{
		if (string.IsNullOrWhiteSpace(raw)) return "";

		var text = raw.Replace("\r\n", "\n").Replace('\r', '\n');

		// The padlock closing a trait-locked stat reads as one glued character ("Chancee").
		text = statEndWordLock.Replace(text, m => m.Groups[1].Value);
		// A padlock before a multiplier reads as a leading digit.
		text = Regex.Replace(text, @"^\d(?=x\s*\d)", "", opts | RegexOptions.IgnoreCase | RegexOptions.Multiline);

		// Rejoin stat names the icon split across two lines.
		foreach (var head in rejoinHeads) {
			text = Regex.Replace(text, $@"\b{head}\s*\n+\s*(?=[A-Za-z])", head + " ", opts | RegexOptions.IgnoreCase);
		}
		text = Regex.Replace(text, @"\bto\s*\n+\s*(?=Grineer|Corpus|Infested)", "to ", opts | RegexOptions.IgnoreCase);

		// Percent recovery.
		text = text.Replace("0/0", "%", StringComparison.Ordinal);
		text = Regex.Replace(text, "[Oo]/[Oo]", "%", opts);
		text = Regex.Replace(text, @"(\d)\s*Z\b", "$1%", opts);

		// Multiplier recovery.
		text = Regex.Replace(text, @"[<\u00ab1]\s*(\d+[.,]\d+)\s+(?=Damage\s+to\s+)", "x$1 ", opts | RegexOptions.IgnoreCase);
		text = Regex.Replace(text, @"\bx\s*O\.", "x0.", opts);
		text = Regex.Replace(text, @"\bx\s*[lI1]\s*[,.]\s*", "x1.", opts);
		text = Regex.Replace(text, @"\bx(\d)\s*,\s*(\d+)", "x$1.$2", opts);
		text = Regex.Replace(text, @"(x\d+)\n(\.\d+)", "$1$2", opts);
		text = Regex.Replace(text, @"\b(x\d+)\s+\.(\d+)", "$1.$2", opts);

		// Decimal and comma normalisation. Comma-to-dot must precede the digit glue.
		text = Regex.Replace(text, @"([+\-\u2013]?\d+),\s+(\d+)\s*%", "$1.$2%", opts);
		text = Regex.Replace(text, @",(\d)", ".$1", opts);
		text = Regex.Replace(text, @"(\d)\s+\.(\d)", "$1.$2", opts);
		text = Regex.Replace(text, @"(\d)\s([1-9])\s*%", "$1.$2%", opts);

		// Digit glue: the reader puts spaces inside numbers.
		for (int i = 0; i < 5; i++) text = Regex.Replace(text, @"([+\-\u2013]\s*\d+)\s+(\d)", "$1$2", opts);
		for (int i = 0; i < 5; i++) text = Regex.Replace(text, @"(\d)\s+(\d)", "$1$2", opts);

		// Combo Duration's seconds unit is not a 5.
		text = Regex.Replace(text, @"(\d)S(?=\s+Combo\s+Dur)", "$1s", opts);
		text = FoldDigitLookalikes(text);
		text = Regex.Replace(text, @"(\d)[A-Za-z](\d)", "$1$2", opts);
		for (int i = 0; i < 3; i++) text = Regex.Replace(text, @"(\d)\s+(\d)", "$1$2", opts);

		foreach (var (pattern, replacement) in aliases) {
			text = pattern.Replace(text, replacement);
		}

		// Qualifiers the card prints in brackets.
		text = Regex.Replace(text, @"\(\s*x\d+\s*(?:for\s*)?Heavy\s*Attack[a-z]*\s*\)?", " ", opts | RegexOptions.IgnoreCase);
		text = Regex.Replace(text, @"\(\s*x\d+\s*fo[a-z]*\s*Bows?\s*\)?", " ", opts | RegexOptions.IgnoreCase);
		text = Regex.Replace(text, @"\(\s*x\d+\s*fo[a-z]*\s*$", " ", opts | RegexOptions.IgnoreCase | RegexOptions.Multiline);
		text = Regex.Replace(text, @"\bx\d+\s*(?:for\s*)?Heavy\s*Attack[a-z]*\b", " ", opts | RegexOptions.IgnoreCase);
		text = Regex.Replace(text, @"\(\s*(\d+[.,]\d+)", "x$1", opts);

		// Symbol and icon-glyph strip.
		text = Regex.Replace(text, @"[*()\[\]{}|\\<>^~]+\s*", " ", opts);
		text = Regex.Replace(text, @"^[A-Za-z]{1,2}\s*(?=[+\-\u2013]\s*\d)", "", opts | RegexOptions.Multiline);
		text = Regex.Replace(text, @"([+\-\u2013]\s*)[A-Z]\s*(\d)", "$1$2", opts);
		text = Regex.Replace(text, @"\b[A-Za-z]{1,2}\s+(?=(?:Slash|Cold|Heat|Electricity|Toxin|Impact|Puncture|Radiation|Viral|Corrosive|Blast|Magnetic|Gas)\b)", "", opts | RegexOptions.IgnoreCase);

		// Re-linearise: every sign or multiplier starts a stat.
		text = Regex.Replace(text, @"\s+([+\-\u2013]\d)", "\n$1", opts);
		text = Regex.Replace(text, @"\s+(x\d)", "\n$1", opts | RegexOptions.IgnoreCase);

		return string.Join('\n', text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
	}

	/// <summary>A letter inside a signed or x value is a misread digit.</summary>
	private static string FoldDigitLookalikes(string text)
	{
		return valueWithLookalike.Replace(text, match => {
			var value = match.Groups[2].Value;
			if (!value.Any(char.IsAsciiDigit)) return match.Value;

			var folded = new string(value.Select(c => c switch {
				'g' or 'q' => '9',
				'l' or 'I' => '1',
				'O' => '0',
				'S' => '5',
				'B' => '8',
				_ => c,
			}).ToArray());
			return match.Groups[1].Value + folded;
		});
	}

	public readonly record struct ParsedValue(bool Positive, double? Value, bool Multiplier);

	/// <summary>Pulls the sign and number out of a fragment. Percent wins, then x, then a signed bare number.</summary>
	public static ParsedValue? ExtractSignAndValue(string fragment)
	{
		if (string.IsNullOrEmpty(fragment)) return null;

		var signs = signBeforeDigits.Matches(fragment);
		bool positive = signs.Count == 0 || (signs[^1].Value != "-" && signs[^1].Value != "\u2013");

		var percents = percentValue.Matches(fragment);
		if (percents.Count > 0 && TryParse(percents[^1].Groups[1].Value, out var percent)) {
			return new ParsedValue(positive, Sanitise(percent), false);
		}

		var multipliers = xValue.Matches(fragment);
		if (multipliers.Count > 0 && TryParse(multipliers[^1].Groups[1].Value, out var factor)) {
			// Polarity for a factor is decided by whether it is above or below 1.
			return new ParsedValue(factor >= 1, factor, true);
		}

		var signed = signedValue.Matches(fragment);
		if (signed.Count > 0 && TryParse(signed[^1].Groups[1].Value, out var plain)) {
			return new ParsedValue(positive, Sanitise(plain), false);
		}

		if (signs.Count > 0 || multipliers.Count > 0) return new ParsedValue(positive, null, false);
		return null;
	}

	private static bool TryParse(string text, out double value)
		=> double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

	/// <summary>Recovers a decimal point the reader dropped ("1234" -> "123.4").</summary>
	public static double Sanitise(double value)
	{
		if (value > maxReasonableValue && value == Math.Floor(value) && value >= 100) {
			var recovered = value / 10;
			if (recovered <= maxReasonableValue) return recovered;
		}

		if (value > 1000 && value != Math.Floor(value)) {
			var digits = ((long)Math.Floor(value)).ToString(CultureInfo.InvariantCulture);
			if (digits.Length > 3) {
				var trimmed = value - (Math.Floor(value / Math.Pow(10, digits.Length - 1)) * Math.Pow(10, digits.Length - 1));
				if (trimmed > 0 && trimmed <= maxReasonableValue) return trimmed;
			}
		}

		return value;
	}

	/// <summary>Parses the repaired text into stats, trying line mode then whole-blob mode.</summary>
	public static List<RivenStat> ParseStats(string rawText)
	{
		var cleaned = Preprocess(rawText);
		if (cleaned.Length == 0) return [];

		var byLine = ParseFromLines(cleaned.Split('\n'));
		if (byLine.Count > 0 && byLine.Any(s => s.Value != null)) return byLine;

		var blob = ParseFromLines([cleaned.Replace('\n', ' ')]);
		return Score(blob) > Score(byLine) ? blob : byLine;
	}

	private static int Score(List<RivenStat> stats) => stats.Sum(s => s.Value != null ? 10 : 3);

	private static List<RivenStat> ParseFromLines(IReadOnlyList<string> rawLines)
	{
		var lines = CollapseOrphanValues(rawLines);
		var results = new List<RivenStat>();

		foreach (var line in lines) {
			if (line.Length == 0) continue;

			var hit = FindStatName(line);
			if (hit == null) continue;

			var (name, index, length) = hit.Value;
			var prefix = line[..index];
			var suffix = line[(index + length)..];

			var parsed = ExtractSignAndValue(prefix) ?? ExtractSignAndValue(suffix);
			bool multiplier = parsed?.Multiplier ?? false;

			// A multiplier on a non-faction stat is bracket junk, not a value.
			if (multiplier && !name.StartsWith("Damage", StringComparison.OrdinalIgnoreCase)) continue;

			bool displayPositive = parsed?.Positive ?? true;
			bool effectivePositive = RivenStatTables.InvertedPolarityStats.Contains(name.ToLowerInvariant())
				? !displayPositive
				: displayPositive;

			var stat = new RivenStat {
				Name = name,
				Positive = effectivePositive,
				Value = parsed?.Value,
				Multiplier = multiplier,
			};
			if (displayPositive != effectivePositive) stat.DisplayPositive = displayPositive;

			Merge(results, stat);
		}

		InheritDamageTypeValues(results, lines);
		return results;
	}

	/// <summary>
	/// A value the reader split onto its own line is attached to the next line that names a stat
	/// but carries no value of its own.
	/// </summary>
	private static List<string> CollapseOrphanValues(IReadOnlyList<string> rawLines)
	{
		var output = new List<string>(rawLines.Count);
		string? orphan = null;

		foreach (var raw in rawLines) {
			var line = raw.Trim();
			if (line.Length == 0) continue;

			bool looksLikeValue = valueOnlyLine.IsMatch(line)
				&& !bareInteger.IsMatch(line)
				&& ExtractSignAndValue(line)?.Value != null
				&& FindStatName(line) == null;

			if (looksLikeValue) {
				orphan = line;
				continue;
			}

			if (orphan != null && FindStatName(line) != null && ExtractSignAndValue(line)?.Value == null) {
				output.Add($"{orphan} {line}");
				orphan = null;
				continue;
			}

			output.Add(line);
		}

		return output;
	}

	/// <summary>
	/// Locates a stat name in a line, returning the canonical name plus the span that actually
	/// matched. The two differ in length: a truncated or misread name is completed from a
	/// shorter fragment, so slicing the line by the canonical name's length ran off the end.
	/// </summary>
	private static (string Name, int Index, int Length)? FindStatName(string line)
	{
		foreach (var stat in RivenStatTables.KnownStats) {
			int index = line.IndexOf(stat, StringComparison.OrdinalIgnoreCase);
			if (index >= 0) return (stat, index, stat.Length);
		}

		// No exact hit: try to complete a truncated name from the text after the value.
		var fragment = valuePrefix.Replace(line, "").Trim();
		if (fragment.Length < 5) return null;

		// The fragment is the line with its value stripped and trimmed, so it is normally a
		// literal substring; checking keeps a surprise from producing a negative span.
		int start = line.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
		if (start < 0) return null;
		int length = Math.Min(fragment.Length, line.Length - start);

		var prefixMatches = RivenStatTables.KnownStats
			.Where(s => s.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)).ToList();
		if (prefixMatches.Count == 1) return (prefixMatches[0], start, length);

		int maxDistance = fragment.Length >= 7 ? 2 : 1;
		string? best = null;
		int bestDistance = int.MaxValue;
		foreach (var stat in RivenStatTables.KnownStats) {
			if (Math.Abs(stat.Length - fragment.Length) > maxDistance) continue;
			int distance = Distance(fragment.ToLowerInvariant(), stat.ToLowerInvariant());
			if (distance <= maxDistance && distance < bestDistance) {
				bestDistance = distance;
				best = stat;
			}
		}

		return best == null ? null : (best, start, length);
	}

	private static void Merge(List<RivenStat> results, RivenStat stat)
	{
		var existing = results.FirstOrDefault(s => string.Equals(s.Name, stat.Name, StringComparison.OrdinalIgnoreCase));
		if (existing == null) {
			results.Add(stat);
			return;
		}

		// A valueless earlier hit is the riven's title spelling a stat; the real line wins.
		if (existing.Value == null && stat.Value != null) {
			existing.Value = stat.Value;
			existing.Positive = stat.Positive;
			existing.DisplayPositive = stat.DisplayPositive;
			existing.Multiplier = stat.Multiplier;
			return;
		}

		// Prefer the decimal reading of the same number.
		if (existing.Value != null && stat.Value != null
			&& Math.Floor(stat.Value.Value) == existing.Value.Value && stat.Value != existing.Value) {
			existing.Value = stat.Value;
		}
	}

	/// <summary>
	/// A combined element ("+90% Heat / Toxin") prints one value for two damage types, so the
	/// second line inherits the first's value and polarity.
	/// </summary>
	private static void InheritDamageTypeValues(List<RivenStat> results, IReadOnlyList<string> lines)
	{
		for (int i = 1; i < results.Count; i++) {
			var stat = results[i];
			if (stat.Value != null) continue;
			if (!RivenStatTables.DamageTypeStats.Contains(stat.Name.ToLowerInvariant())) continue;

			var previous = results[i - 1];
			if (!RivenStatTables.DamageTypeStats.Contains(previous.Name.ToLowerInvariant())) continue;
			if (previous.Value == null || previous.Multiplier) continue;

			var line = lines.FirstOrDefault(l => l.Contains(stat.Name, StringComparison.OrdinalIgnoreCase));
			if (line != null) {
				int index = line.IndexOf(stat.Name, StringComparison.OrdinalIgnoreCase);
				if (index > 0 && noisySign.IsMatch(line[..index])) continue;
			}

			stat.Value = previous.Value;
			stat.Positive = previous.Positive;
		}
	}

	private static int Distance(string a, string b)
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

	/// <summary>A card carries at most three buffs and one curse; fewer than two buffs is a short read.</summary>
	public static bool IsIncompleteRead(IReadOnlyList<RivenStat> stats)
	{
		if (stats.Count == 0) return false;
		if (stats.Count < 2) return true;
		if (stats.Count(s => s.Positive) < 2) return true;
		// More than four means the crop spanned two cards.
		return stats.Count > 4;
	}
}
