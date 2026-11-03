using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace framenion.Src.Riven;

/// <summary>
/// Community-maintained "best riven rolls" reference, read from a public Google Sheet (by
/// Megrim, Valkyrial and Lia): one row per weapon with the positives and negatives worth hunting
/// for, plus an abbreviation legend (MS = Multishot, CD = Critical Damage, ...).
/// https://docs.google.com/spreadsheets/d/1OQGKpWXeoPaN0Cy7mTvVZMRcwvZXgIC3EO1AIRAkwDg
///
/// Downloaded through the sheet's public CSV export, which needs no API key or credentials as
/// long as the document stays shared "anyone with the link can view". Every tab is hashed on
/// each sync so a re-download only overwrites the cache, and triggers a re-parse, on an actual
/// edit to the sheet; an unreachable network just keeps serving the last cached copy.
/// </summary>
public static partial class RivenRollGuide
{
	private const string spreadsheetId = "1OQGKpWXeoPaN0Cy7mTvVZMRcwvZXgIC3EO1AIRAkwDg";

	/// <summary>The weapon roll table; the only tab this class parses.</summary>
	private const string rollsSheet = "Rolls";

	/// <summary>
	/// Physical-damage-type presence, vintage-only stats and riven naming syllables. Cached
	/// alongside the roll table for later use, but not parsed here.
	/// </summary>
	private static readonly string[] referenceSheets = ["IPS", "Vintage", "Names"];

	private static IEnumerable<string> AllSheets => [rollsSheet, .. referenceSheets];

	private static string CacheDir => Path.Combine(AppData.CacheDir, "riven-rolls");
	private static string HashesPath => Path.Combine(CacheDir, "hashes.txt");
	private static string CsvPath(string sheet) => Path.Combine(CacheDir, $"{sheet}.csv");

	/// <summary>
	/// Abbreviation to full stat name, e.g. "MS" -&gt; "Multishot". Not unique by design: the
	/// sheet reuses a few abbreviations for different stats depending on weapon class (EFF is
	/// both Ammo Efficiency and Heavy Attack Efficiency), so this keeps whichever mapping the
	/// legend lists first.
	/// </summary>
	public static FrozenDictionary<string, string> Abbreviations { get; private set; } = FrozenDictionary<string, string>.Empty;

	/// <summary>Weapon name, keyed by <see cref="RivenData.Normalize"/>, to its roll option(s).</summary>
	public static FrozenDictionary<string, IReadOnlyList<RivenRollOption>> BestRolls { get; private set; } = FrozenDictionary<string, IReadOnlyList<RivenRollOption>>.Empty;

	public static IReadOnlyList<RivenRollOption>? FindBestRolls(string weaponName)
		=> BestRolls.TryGetValue(RivenData.Normalize(weaponName), out var options) ? options : null;

	/// <summary>
	/// Downloads every tab, keeping the previous cache for any tab that fails to fetch or has
	/// not changed. Safe to call with no network: it just leaves the cache as it was.
	/// </summary>
	public static async Task SyncAsync()
	{
		Directory.CreateDirectory(CacheDir);

		var hashes = await ReadHashesAsync();
		bool changed = false;

		foreach (var sheet in AllSheets) {
			string csv;
			try {
				csv = await FetchSheetAsync(sheet);
			} catch (Exception ex) {
				Log.Warn($"Could not fetch the riven rolls sheet tab '{sheet}'; keeping the cached copy.", ex);
				continue;
			}

			string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csv)));
			if (File.Exists(CsvPath(sheet)) && hashes.TryGetValue(sheet, out var existing) && existing == hash) continue;

			await WriteAtomicAsync(CsvPath(sheet), csv);
			hashes[sheet] = hash;
			changed = true;
			Log.Info($"Riven rolls sheet tab '{sheet}' updated.");
		}

		if (changed) await WriteHashesAsync(hashes);
	}

	/// <summary>Parses whatever is currently cached. Call after <see cref="SyncAsync"/>, or on its own offline.</summary>
	public static void Load()
	{
		var rollsPath = CsvPath(rollsSheet);
		if (!File.Exists(rollsPath)) {
			Log.Warn("No cached riven rolls sheet yet; best-roll suggestions will be unavailable.");
			return;
		}

		try {
			var rows = ParseCsv(File.ReadAllText(rollsPath));
			var (abbreviations, bestRolls) = ParseRollsSheet(rows);
			Abbreviations = abbreviations;
			BestRolls = bestRolls;
			Log.Info($"Riven rolls sheet loaded: {Abbreviations.Count} abbreviation(s), {BestRolls.Count} weapon(s).");
		} catch (Exception ex) {
			Log.Warn("Could not parse the cached riven rolls sheet.", ex);
		}
	}

	private static async Task<string> FetchSheetAsync(string sheet)
	{
		// The gviz endpoint serves a public sheet's CSV by tab name with no auth and no API key,
		// unlike the plain /export endpoint, which only takes a numeric gid.
		string url = $"https://docs.google.com/spreadsheets/d/{spreadsheetId}/gviz/tq?tqx=out:csv&sheet={Uri.EscapeDataString(sheet)}";
		using var stream = await AppData.GetStreamAsync(url);
		using var reader = new StreamReader(stream, Encoding.UTF8);
		return await reader.ReadToEndAsync();
	}

	private static async Task<Dictionary<string, string>> ReadHashesAsync()
	{
		var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!File.Exists(HashesPath)) return hashes;

		try {
			foreach (var line in await File.ReadAllLinesAsync(HashesPath)) {
				var parts = line.Split('!', 2);
				if (parts.Length == 2) hashes[parts[0]] = parts[1];
			}
		} catch (Exception ex) {
			Log.Warn("Could not read the cached riven rolls sheet hashes.", ex);
		}
		return hashes;
	}

	private static async Task WriteHashesAsync(Dictionary<string, string> hashes)
	{
		try {
			var lines = hashes.Select(kv => $"{kv.Key}!{kv.Value}").OrderBy(l => l, StringComparer.Ordinal);
			await File.WriteAllLinesAsync(HashesPath, lines);
		} catch (Exception ex) {
			Log.Error("Could not write the riven rolls sheet hashes.", ex);
		}
	}

	/// <summary>Writes via a temp file so an interrupted write cannot leave a truncated cache entry.</summary>
	private static async Task WriteAtomicAsync(string destination, string content)
	{
		var temp = destination + ".part";
		try {
			await File.WriteAllTextAsync(temp, content, Encoding.UTF8);
			File.Move(temp, destination, overwrite: true);
		} finally {
			if (File.Exists(temp)) {
				try { File.Delete(temp); } catch { /* best effort */ }
			}
		}
	}

	// ---- Parsing ----

	/// <summary>
	/// Reads the abbreviation legend (the rows above the weapon table) and every weapon's roll
	/// option(s). Unrecognized rows are skipped rather than thrown on: the sheet is hand-edited
	/// and not guaranteed to hold the exact same shape from one community update to the next.
	/// </summary>
	private static (FrozenDictionary<string, string> Abbreviations, FrozenDictionary<string, IReadOnlyList<RivenRollOption>> BestRolls) ParseRollsSheet(List<string[]> rows)
	{
		int headerRow = rows.FindIndex(r => Col(r, 0).Equals("Name", StringComparison.OrdinalIgnoreCase)
			&& Col(r, 1).StartsWith("Positives", StringComparison.OrdinalIgnoreCase));

		var abbreviations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		int legendEnd = headerRow < 0 ? rows.Count : headerRow;
		// Row 0 is the "ABBREVIATIONS" title; each row after it packs three (full name, abbreviation)
		// pairs, with the middle pair's columns in the opposite order from the outer two.
		for (int i = 1; i < legendEnd; i++) {
			var row = rows[i];
			AddAbbreviation(abbreviations, full: Col(row, 0), abbrev: Col(row, 1));
			AddAbbreviation(abbreviations, full: Col(row, 3), abbrev: Col(row, 2));
			AddAbbreviation(abbreviations, full: Col(row, 4), abbrev: Col(row, 5));
		}

		var bestRolls = new Dictionary<string, List<RivenRollOption>>(StringComparer.Ordinal);
		if (headerRow >= 0) ParseWeaponRows(rows, headerRow + 1, bestRolls, abbreviations);

		return (
			abbreviations.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
			bestRolls.ToFrozenDictionary(kv => kv.Key, kv => (IReadOnlyList<RivenRollOption>)kv.Value, StringComparer.Ordinal)
		);
	}

	private static void ParseWeaponRows(List<string[]> rows, int startRow, Dictionary<string, List<RivenRollOption>> bestRolls, IReadOnlyDictionary<string, string> abbreviations)
	{
		string? currentKey = null;

		for (int i = startRow; i < rows.Count; i++) {
			var row = rows[i];
			string name = Col(row, 0);

			if (name.StartsWith('[') && name.EndsWith(']')) {
				currentKey = RivenData.Normalize(name[1..^1]);
				bestRolls[currentKey] = [];
			}
			if (currentKey == null) continue;

			var positives = (string[])[Col(row, 1), Col(row, 2), Col(row, 3)];
			positives = [.. positives.Where(p => p.Length > 0)];
			for (int j = 0; j < positives.Length; j++) {
				positives[j] = $"+ {ExpandAbbreviations(positives[j], abbreviations)}";
			}
			string negatives = ExpandAbbreviations(Col(row, 6), abbreviations);
			string notes = string.Join(' ', ((string[])[Col(row, 8), Col(row, 9), Col(row, 10)]).Where(n => n.Length > 0));

			var options = bestRolls[currentKey];
			if (positives.Length == 0 && negatives.Length == 0) {
				// A row with nothing but a note continues the previous option's note: the sheet
				// authors wrap a long remark onto its own row instead of one wrapped cell.
				if (notes.Length > 0 && options.Count > 0) options[^1] = options[^1].WithAppendedNote(notes);
				continue;
			}

			options.Add(new RivenRollOption(positives, negatives, notes));
		}
	}

	private static void AddAbbreviation(Dictionary<string, string> map, string full, string abbrev)
	{
		if (full.Length == 0 || abbrev.Length == 0 || abbrev.Length > full.Length) return;
		map.TryAdd(abbrev, full);
	}

	/// <summary>
	/// Expands every abbreviation in a priority chain (e.g. "CC* &gt; PUNC") to its full name,
	/// without disturbing the "&gt;"/"/" structure or a token's own trailing marker. Only the run
	/// of capital letters at the front of a token is looked up, so "CC*" becomes "Critical
	/// Chance*" rather than being left abbreviated for want of an exact whole-token match.
	/// </summary>
	private static string ExpandAbbreviations(string chain, IReadOnlyDictionary<string, string> abbreviations)
		=> chain.Length == 0 ? chain : AbbreviationToken().Replace(chain, m => abbreviations.TryGetValue(m.Value, out var full) ? full : m.Value);

	[GeneratedRegex("[A-Z]+")]
	private static partial Regex AbbreviationToken();

	/// <summary>
	/// Collapses a weapon's roll options for display: an option with no negative of its own
	/// folds its positives into the previous option that had one, so that negative prints once,
	/// after both sets of positives, instead of sitting between them.
	/// </summary>
	public static IReadOnlyList<RivenRollGroup> BuildGroups(IReadOnlyList<RivenRollOption> options)
	{
		var groups = new List<(List<string> Positives, string Negatives)>();

		foreach (var option in options) {
			if (groups.Count == 0 || option.HasNegatives) groups.Add(([], option.Negatives));
			if (option.HasPositives) groups[^1].Positives.Add(option.PositivesText);
		}

		// A blank line between options folded into the same group, so a reader can still tell
		// "p1" apart from "p2" even though they now share one negative printed after both.
		return [.. groups.Select(g => new RivenRollGroup(string.Join("\n\n", g.Positives), g.Negatives))];
	}

	private static string Col(string[] row, int index) => index < row.Length ? row[index].Trim() : "";

	/// <summary>Minimal RFC 4180 reader: quoted fields may embed commas, quotes ("") and newlines.</summary>
	private static List<string[]> ParseCsv(string text)
	{
		var rows = new List<string[]>();
		var fields = new List<string>();
		var field = new StringBuilder();
		bool inQuotes = false;

		for (int i = 0; i < text.Length; i++) {
			char c = text[i];
			if (inQuotes) {
				if (c != '"') {
					field.Append(c);
				} else if (i + 1 < text.Length && text[i + 1] == '"') {
					field.Append('"');
					i++;
				} else {
					inQuotes = false;
				}
				continue;
			}

			switch (c) {
				case '"':
					inQuotes = true;
					break;
				case ',':
					fields.Add(field.ToString());
					field.Clear();
					break;
				case '\r':
					break;
				case '\n':
					fields.Add(field.ToString());
					field.Clear();
					rows.Add([.. fields]);
					fields.Clear();
					break;
				default:
					field.Append(c);
					break;
			}
		}

		if (field.Length > 0 || fields.Count > 0) {
			fields.Add(field.ToString());
			rows.Add([.. fields]);
		}
		return rows;
	}
}
