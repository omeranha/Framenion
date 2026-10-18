using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace framenion.Src.Riven;

/// <summary>
/// Riven-capable weapons and their stat pools, indexed from DE's official export.
/// Dispositions come from <c>omegaAttenuation</c>; stat base values from the "Randomized"
/// upgrade entries.
/// </summary>
public static class RivenData
{
	public const string RifleKey = "/Lotus/Upgrades/Mods/Randomized/LotusRifleRandomModRare";
	public const string PistolKey = "/Lotus/Upgrades/Mods/Randomized/LotusPistolRandomModRare";
	public const string MeleeKey = "/Lotus/Upgrades/Mods/Randomized/PlayerMeleeWeaponRandomModRare";
	public const string ShotgunKey = "/Lotus/Upgrades/Mods/Randomized/LotusShotgunRandomModRare";
	public const string ArchgunKey = "/Lotus/Upgrades/Mods/Randomized/LotusArchgunRandomModRare";
	public const string KitgunKey = "/Lotus/Upgrades/Mods/Randomized/LotusModularPistolRandomModRare";
	public const string ZawKey = "/Lotus/Upgrades/Mods/Randomized/LotusModularMeleeRandomModRare";

	private static readonly object gate = new();
	private static bool built;

	private static FrozenDictionary<string, RivenWeapon> weaponsByName = FrozenDictionary<string, RivenWeapon>.Empty;
	private static FrozenDictionary<string, RivenWeapon> weaponsByNormalized = FrozenDictionary<string, RivenWeapon>.Empty;
	private static string[] weaponNames = [];

	public static IReadOnlyList<string> WeaponNames
	{
		get {
			EnsureBuilt();
			return weaponNames;
		}
	}

	public static bool IsAvailable
	{
		get {
			EnsureBuilt();
			return weaponsByName.Count > 0 && GameData.RivenMods.Count > 0;
		}
	}

	/// <summary>Rebuilds the index. Call after the export has been parsed.</summary>
	public static void Invalidate()
	{
		lock (gate) {
			built = false;
		}
	}

	private static void EnsureBuilt()
	{
		if (built) return;
		lock (gate) {
			if (built) return;

			var byName = new Dictionary<string, RivenWeapon>(StringComparer.OrdinalIgnoreCase);
			var byNormalized = new Dictionary<string, RivenWeapon>(StringComparer.Ordinal);

			foreach (var (uniqueName, weapon) in GameData.ExportWeapons.Concat(GameData.ExportSentinels)) {
				if (weapon.OmegaAttenuation is not > 0) continue;
				if (string.IsNullOrEmpty(weapon.Name)) continue;

				var name = weapon.Name.Contains('<') ? CleanName(weapon.Name) : weapon.Name;
				var info = new RivenWeapon(name, uniqueName, weapon.OmegaAttenuation.Value, weapon.Category, weapon.Multishot ?? 1);

				// Duviri exports a Drifter twin of several Tenno melees at a flat 0.5 dispo;
				// the riven belongs to the Tenno entry, so that one must win the name key.
				if (byName.TryGetValue(name, out var existing) && Rank(existing.Category) > Rank(info.Category)) continue;

				byName[name] = info;
				byNormalized[Normalize(name)] = info;
			}

			weaponsByName = byName.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
			weaponsByNormalized = byNormalized.ToFrozenDictionary(StringComparer.Ordinal);
			weaponNames = [.. byName.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
			built = true;

			Log.Info($"Riven data: {weaponsByName.Count} weapons with a disposition, {GameData.RivenMods.Count} stat pools.");
		}
	}

	private static int Rank(string category) => category == "DrifterMelee" ? 0 : 1;

	private static string CleanName(string name)
	{
		var builder = new StringBuilder(name.Length);
		bool inTag = false;
		foreach (char c in name) {
			if (c == '<') { inTag = true; continue; }
			if (inTag) { if (c == '>') inTag = false; continue; }
			if (c == ' ' && (builder.Length == 0 || builder[^1] == ' ')) continue;
			builder.Append(c);
		}
		return builder.ToString().TrimEnd();
	}

	public static string Normalize(string text)
	{
		if (string.IsNullOrEmpty(text)) return "";

		var builder = new StringBuilder(text.Length);
		bool lastSpace = true;
		foreach (char c in text.ToLowerInvariant()) {
			if (char.IsAsciiLetterOrDigit(c)) {
				builder.Append(c);
				lastSpace = false;
			} else if (!lastSpace) {
				builder.Append(' ');
				lastSpace = true;
			}
		}
		return builder.ToString().Trim();
	}

	public static RivenWeapon? FindWeapon(string name)
	{
		EnsureBuilt();
		if (string.IsNullOrWhiteSpace(name)) return null;
		if (weaponsByName.TryGetValue(name.Trim(), out var exact)) return exact;
		return weaponsByNormalized.TryGetValue(Normalize(name), out var normalized) ? normalized : null;
	}

	public static double? GetDisposition(string weaponName) => FindWeapon(weaponName)?.Disposition;

	/// <summary>True when the card reads "Melee Damage" and "Attack Speed" for this weapon.</summary>
	public static bool IsMelee(string weaponName)
	{
		var info = FindWeapon(weaponName);
		if (info == null) return false;
		if (info.UniqueName.Contains("ModularMelee", StringComparison.OrdinalIgnoreCase)) return true;
		return info.Category is "Melee" or "SpaceMelee" or "DrifterMelee"
			|| info.UniqueName.Contains("/Melee/", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// Candidate stat pools for a weapon, best guess first. DE's official export carries no
	/// holster category or compatibility tags, so a shotgun cannot be told from a rifle by
	/// metadata alone; the grader picks between the candidates by which one makes the rolled
	/// values fall inside their legal range.
	/// </summary>
	public static IReadOnlyList<string> CandidateRivenTypes(string weaponName)
	{
		var info = FindWeapon(weaponName);
		if (info == null) return [];

		// Modular weapons are identified by path, and DE mis-files some Zaw parts under Pistols.
		if (info.UniqueName.Contains("ModularMelee", StringComparison.OrdinalIgnoreCase)) return [ZawKey];
		if (info.UniqueName.Contains("SUModularSecondary", StringComparison.OrdinalIgnoreCase)
			|| info.UniqueName.Contains("InfKitGun", StringComparison.OrdinalIgnoreCase)) return [KitgunKey];

		bool shotgunPath = info.UniqueName.Contains("/Shotgun", StringComparison.OrdinalIgnoreCase);

		switch (info.Category) {
			case "LongGuns":
				// Multishot above 1 is the strongest hint DE's export gives for a shotgun.
				return shotgunPath || info.Multishot > 1 ? [ShotgunKey, RifleKey] : [RifleKey, ShotgunKey];
			case "Pistols":
				return [PistolKey, KitgunKey];
			case "Melee":
			case "DrifterMelee":
				return [MeleeKey, ZawKey];
			case "SpaceMelee":
				return [MeleeKey];
			case "SpaceGuns":
				return [ArchgunKey];
			case "SentinelWeapons":
				// Companion weapons have no pool of their own; they roll the class whose mods
				// they equip, which the export does not state, so all four stay candidates.
				return shotgunPath || info.Multishot > 1
					? [ShotgunKey, RifleKey, PistolKey, MeleeKey]
					: [RifleKey, ShotgunKey, PistolKey, MeleeKey];
			default:
				return [];
		}
	}

	public static RivenModType? GetRivenType(string rivenTypeKey)
		=> GameData.RivenMods.TryGetValue(rivenTypeKey, out var type) ? type : null;

	/// <summary>Finds a stat's base value within a pool, tolerating the melee tag variants.</summary>
	public static RivenUpgradeEntry? FindUpgradeEntry(string rivenTypeKey, string tag)
	{
		var type = GetRivenType(rivenTypeKey);
		if (type == null) return null;

		var found = type.Entries.FirstOrDefault(e => e.Tag == tag);
		if (found != null) return found;

		// Melee faction damage uses its own tags.
		var meleeFaction = tag.Replace("WeaponFaction", "WeaponMeleeFaction", StringComparison.Ordinal);
		found = type.Entries.FirstOrDefault(e => e.Tag == meleeFaction);
		if (found != null) return found;

		if (tag == "WeaponDamageAmountMod") {
			found = type.Entries.FirstOrDefault(e => e.Tag == "WeaponMeleeDamageMod");
		} else if (tag == "WeaponMeleeDamageMod") {
			found = type.Entries.FirstOrDefault(e => e.Tag == "WeaponDamageAmountMod");
		}
		return found;
	}

	/// <summary>
	/// Rebuilds the name the game gives a riven. The syllables are in the export's prefixTag and
	/// suffixTag, and the ordering is by roll strength rather than by stat: the strongest buff
	/// contributes the leading prefix, the weakest contributes the trailing suffix, and anything
	/// between joins on a hyphen. The curse never appears in the name.
	/// </summary>
	public static string BuildRivenName(string rivenTypeKey, IReadOnlyList<(string Tag, double RollFloat)> buffs, bool melee)
	{
		if (buffs.Count == 0) return "";

		var resolved = buffs
			.Select(b => (b.RollFloat, Entry: FindUpgradeEntry(rivenTypeKey, RivenStatTables.WeaponDamageTag(b.Tag, melee))))
			.Where(x => x.Entry != null)
			.OrderByDescending(x => x.RollFloat)
			.ThenBy(x => x.Entry!.BaseValue)
			.Select(x => x.Entry!)
			.ToList();

		if (resolved.Count == 0) return "";
		if (resolved.Count == 1) {
			return resolved[0].Prefix.Length == 0 ? "" : TitleCase(resolved[0].Prefix) + resolved[0].Suffix.ToLowerInvariant();
		}

		var name = new StringBuilder();
		for (int i = 0; i < resolved.Count; i++) {
			var entry = resolved[i];
			if (i == resolved.Count - 1) {
				name.Append(entry.Suffix.ToLowerInvariant());
			} else if (name.Length == 0) {
				if (entry.Prefix.Length > 0) name.Append(TitleCase(entry.Prefix));
			} else if (entry.Prefix.Length > 0) {
				name.Append('-').Append(entry.Prefix.ToLowerInvariant());
			}
		}
		return name.ToString();
	}

	private static string TitleCase(string value)
		=> value.Length == 0 ? "" : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

	/// <summary>
	/// Same-family weapons (Braton / Braton Prime / MK1-Braton). The reroll screen names the
	/// family but the card uses the specific variant's disposition.
	/// </summary>
	public static IReadOnlyList<RivenWeapon> GetFamilyVariants(string weaponName)
	{
		EnsureBuilt();
		var info = FindWeapon(weaponName);
		if (info == null) return [];

		var core = FamilyCore(info.Name);
		if (core.Length == 0) return [];

		return [.. weaponsByName.Values
			.Where(w => !string.Equals(w.Name, info.Name, StringComparison.OrdinalIgnoreCase) && FamilyCore(w.Name) == core)
			.DistinctBy(w => w.Name, StringComparer.OrdinalIgnoreCase)];
	}

	private static readonly string[] variantWords = [
		"prime", "wraith", "vandal", "prisma", "mk1", "mk 1", "kuva", "tenet", "coda", "dex", "rakta",
		"telos", "synoid", "sancti", "vaykor", "secura", "carmine", "ceti", "mara", "primed",
	];

	/// <summary>Strips variant prefixes/suffixes so "Braton Prime" and "MK1-Braton" share a key.</summary>
	public static string FamilyCore(string weaponName)
	{
		var words = Normalize(weaponName).Split(' ', StringSplitOptions.RemoveEmptyEntries)
			.Where(w => !variantWords.Contains(w, StringComparer.Ordinal))
			.ToArray();
		return words.Length == 0 ? "" : string.Join(' ', words);
	}

	/// <summary>Matches an OCR'd weapon label against the index, exact first then fuzzy.</summary>
	public static (string Name, bool Exact)? FindWeaponByLabel(IEnumerable<string> candidateLines)
	{
		EnsureBuilt();

		(string Name, bool Exact)? best = null;
		int bestDistance = int.MaxValue;

		foreach (var line in candidateLines) {
			var normalized = Normalize(line);
			if (normalized.Length < 3) continue;

			if (weaponsByNormalized.TryGetValue(normalized, out var exact)) {
				return (exact.Name, true);
			}

			foreach (var (key, weapon) in weaponsByNormalized) {
				// Guard the edit-distance sweep: lengths have to be comparable.
				if (Math.Abs(key.Length - normalized.Length) > 3) continue;

				int maxDistance = key.Length >= 10 ? 3 : key.Length >= 6 ? 2 : 1;
				int distance = Levenshtein(normalized, key, maxDistance);
				if (distance <= maxDistance && distance < bestDistance) {
					bestDistance = distance;
					best = (weapon.Name, false);
				}
			}
		}

		return best;
	}

	/// <summary>Last resort: look for any weapon name inside the card text.</summary>
	public static string? FindWeaponInText(string text)
	{
		EnsureBuilt();
		var normalized = Normalize(text);
		if (normalized.Length == 0) return null;

		string? best = null;
		foreach (var (key, weapon) in weaponsByNormalized) {
			if (key.Length < 4) continue;
			if (!normalized.Contains(key, StringComparison.Ordinal)) continue;
			if (best == null || weapon.Name.Length > best.Length) best = weapon.Name;
		}
		return best;
	}

	private static int Levenshtein(string a, string b, int cap)
	{
		var previous = new int[b.Length + 1];
		var current = new int[b.Length + 1];
		for (int j = 0; j <= b.Length; j++) previous[j] = j;

		for (int i = 1; i <= a.Length; i++) {
			current[0] = i;
			int rowBest = current[0];
			for (int j = 1; j <= b.Length; j++) {
				int cost = a[i - 1] == b[j - 1] ? 0 : 1;
				current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
				rowBest = Math.Min(rowBest, current[j]);
			}
			if (rowBest > cap) return cap + 1;
			(previous, current) = (current, previous);
		}
		return previous[b.Length];
	}
}
