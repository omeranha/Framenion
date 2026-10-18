using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion.Src.Riven;

/// <summary>
/// Reads owned riven mods out of the inventory dump. Everything the grader needs is in the
/// fingerprint, so unlike the screen scanner this path needs no inference: compat names the
/// weapon exactly, ItemType names the stat pool exactly, and each stat carries its raw roll.
/// </summary>
public static class RivenInventory
{
	/// <summary>
	/// Fingerprint roll values are an integer fraction of this, which is the roll's position in
	/// its legal range (the 0.9..1.1 lerp the grading formula applies).
	/// </summary>
	private const double fingerprintScale = 0x3FFFFFFF;

	/// <summary>Rivens are quoted at max rank, the same as every trade and riven market listing.</summary>
	private const int displayRank = RivenGrading.MaxRivenModRank;

	private static readonly Dictionary<string, string> polarityNames = new(StringComparer.Ordinal) {
		["AP_ATTACK"] = "Madurai",
		["AP_TACTIC"] = "Naramon",
		["AP_DEFENSE"] = "Vazarin",
		["AP_POWER"] = "Zenurik",
		["AP_WARD"] = "Unairu",
		["AP_PRECEPT"] = "Penjaga",
		["AP_UMBRA"] = "Umbra",
		["AP_UNIVERSAL"] = "Universal",
	};

	/// <summary>Filter categories, in the order the tab shows them.</summary>
	public static readonly string[] Categories = ["Rifle", "Shotgun", "Pistol", "Melee", "Archgun", "Modular", "Companion"];

	private static string CategoryOf(string rivenTypeKey) => rivenTypeKey switch {
		RivenData.RifleKey => "Rifle",
		RivenData.ShotgunKey => "Shotgun",
		RivenData.PistolKey => "Pistol",
		RivenData.MeleeKey => "Melee",
		RivenData.ArchgunKey => "Archgun",
		// Zaws and Kitguns are both built weapons; one filter covers them.
		RivenData.ZawKey or RivenData.KitgunKey => "Modular",
		_ => "Companion",
	};

	private static bool IsMeleePool(string rivenTypeKey)
		=> rivenTypeKey == RivenData.MeleeKey || rivenTypeKey == RivenData.ZawKey;

	public static async Task<List<OwnedRiven>> BuildAsync(JsonElement root)
	{
		if (!root.TryGetProperty("Upgrades", out var upgrades) || upgrades.ValueKind != JsonValueKind.Array) {
			Log.Info("[Rivens] Inventory dump has no Upgrades array.");
			return [];
		}

		var parsed = new List<ParsedRiven>();
		int skipped = 0;
		foreach (var entry in upgrades.EnumerateArray()) {
			if (!entry.TryGetProperty("ItemType", out var typeEl) || typeEl.GetString() is not string itemType) continue;
			if (!itemType.Contains("/Randomized/", StringComparison.Ordinal)) continue;

			var riven = Parse(entry, itemType);
			if (riven == null) {
				skipped++;
				continue;
			}
			parsed.Add(riven);
		}

		if (parsed.Count == 0) {
			if (skipped > 0) Log.Warn($"[Rivens] Could not read any of the {skipped} riven mod(s) found.");
			return [];
		}

		// The weapon icons are not all in the equipment catalogue: a Zaw tip and a Kitgun
		// barrel carry the disposition but are filtered out of the equipment list, so their
		// icons were never fetched.
		await GameData.DownloadIconsAsync(parsed.Select(p => p.IconUrl));

		var rivens = parsed.Select(Grade).ToList();
		rivens.Sort((a, b) => string.Compare(a.WeaponName, b.WeaponName, StringComparison.OrdinalIgnoreCase));

		Log.Info($"[Rivens] {rivens.Count} owned ({rivens.Count(r => r.Veiled)} veiled){(skipped > 0 ? $", {skipped} unreadable" : "")}.");
		return rivens;
	}

	private sealed record ParsedRiven(
		string Id,
		string TypeKey,
		string WeaponName,
		string IconUrl,
		double Disposition,
		int MasteryRank,
		int Rerolls,
		string Polarity,
		List<(string Tag, double RollFloat)> Buffs,
		List<(string Tag, double RollFloat)> Curses);

	private static ParsedRiven? Parse(JsonElement entry, string itemType)
	{
		if (!entry.TryGetProperty("UpgradeFingerprint", out var fpEl) || fpEl.GetString() is not string fingerprint || fingerprint.Length == 0) {
			return null;
		}

		try {
			using var doc = JsonDocument.Parse(fingerprint);
			var fp = doc.RootElement;

			// A veiled riven has no compat and no stats; it only carries its unveiling challenge.
			string compat = fp.TryGetProperty("compat", out var compatEl) ? compatEl.GetString() ?? "" : "";

			var weapon = compat.Length > 0 ? FindWeapon(compat) : null;
			if (compat.Length > 0 && weapon == null) {
				Log.Warn($"[Rivens] Unknown compat weapon '{compat}'; skipping.");
				return null;
			}

			return new ParsedRiven(
				Id: entry.TryGetProperty("ItemId", out var idEl) && idEl.TryGetProperty("$oid", out var oidEl)
					? oidEl.GetString() ?? "" : "",
				TypeKey: itemType,
				WeaponName: weapon?.Name ?? "Veiled riven",
				IconUrl: weapon?.Icon ?? "",
				Disposition: weapon?.Disposition ?? 1.0,
				MasteryRank: fp.TryGetProperty("lvlReq", out var lvlEl) && lvlEl.TryGetInt32(out var lvl) ? lvl : 0,
				Rerolls: fp.TryGetProperty("rerolls", out var rrEl) && rrEl.TryGetInt32(out var rr) ? rr : 0,
				Polarity: fp.TryGetProperty("pol", out var polEl) && polEl.GetString() is string pol
					? polarityNames.TryGetValue(pol, out var name) ? name : pol
					: "",
				Buffs: ReadStats(fp, "buffs"),
				Curses: ReadStats(fp, "curses"));
		} catch (Exception ex) {
			Log.Warn($"[Rivens] Could not parse a riven fingerprint: {ex.Message}");
			return null;
		}
	}

	private static List<(string Tag, double RollFloat)> ReadStats(JsonElement fingerprint, string property)
	{
		var stats = new List<(string, double)>();
		if (!fingerprint.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) return stats;

		foreach (var stat in array.EnumerateArray()) {
			if (stat.TryGetProperty("Tag", out var tagEl) && tagEl.GetString() is string tag && tag.Length > 0
				&& stat.TryGetProperty("Value", out var valueEl) && valueEl.TryGetDouble(out var value)) {
				stats.Add((tag, value / fingerprintScale));
			}
		}
		return stats;
	}

	private static (string Name, string Icon, double Disposition)? FindWeapon(string uniqueName)
	{
		if (!GameData.ExportWeapons.TryGetValue(uniqueName, out var weapon)
			&& !GameData.ExportSentinels.TryGetValue(uniqueName, out weapon)) {
			return null;
		}

		// The catalogue holds the cleaned display name; the raw DTO name still has the icon
		// placeholders ("<ARCHWING> Corvas") in it.
		string name = GameData.Catalog.TryGetValue(uniqueName, out var entry) ? entry.Name : weapon.Name;
		return (name, weapon.Icon, weapon.OmegaAttenuation ?? 1.0);
	}

	private static OwnedRiven Grade(ParsedRiven parsed)
	{
		bool veiled = parsed.Buffs.Count == 0;
		bool melee = IsMeleePool(parsed.TypeKey);
		var grade = veiled
			? null
			: RivenGrading.GradeKnown(parsed.TypeKey, parsed.Disposition, melee, displayRank, parsed.Buffs, parsed.Curses);

		return new OwnedRiven {
			Id = parsed.Id,
			WeaponName = parsed.WeaponName,
			RivenName = veiled ? "" : RivenData.BuildRivenName(parsed.TypeKey, parsed.Buffs, melee),
			ClassName = RivenData.GetRivenType(parsed.TypeKey)?.Name ?? CategoryOf(parsed.TypeKey),
			Category = CategoryOf(parsed.TypeKey),
			IconPath = GameData.GetLocalIconPath(parsed.IconUrl),
			Disposition = parsed.Disposition,
			MasteryRank = parsed.MasteryRank,
			Rerolls = parsed.Rerolls,
			Polarity = parsed.Polarity,
			Veiled = veiled,
			OverallGrade = grade?.OverallGrade ?? "",
			StatPerfectness = grade?.StatPerfectness ?? 0,
			Stats = grade?.Stats ?? [],
		};
	}
}
