using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace framenion.Src.Riven;

/// <summary>
/// Stat vocabulary for riven cards: display names, upgrade tags, and which tags the card prints
/// as a flat number or a multiplier rather than a percentage.
/// </summary>
public static partial class RivenStatTables
{
	/// <summary>
	/// Longest-first: the parser takes the first substring hit, so "Critical Chance for Slide
	/// Attack" has to be tested before "Critical Chance".
	/// </summary>
	public static readonly string[] KnownStats = [
		"Additional Combo Count Chance",
		"Chance to Gain Combo Count",
		"Critical Chance for Slide Attack",
		"Heavy Attack Efficiency",
		"Magazine Capacity",
		"Damage to Grineer",
		"Damage to Corpus",
		"Damage to Infested",
		"Critical Chance",
		"Critical Damage",
		"Finisher Damage",
		"Melee Damage",
		"Weapon Recoil",
		"Status Duration",
		"Status Chance",
		"Projectile Speed",
		"Reload Speed",
		"Attack Speed",
		"Flight Speed",
		"Fire Rate",
		"Punch Through",
		"Combo Duration",
		"Initial Combo",
		"Ammo Maximum",
		"Heavy Attack",
		"Channeling Damage",
		"Channeling Efficiency",
		"Multishot",
		"Electricity",
		"Corrosive",
		"Radiation",
		"Magnetic",
		"Cold",
		"Heat",
		"Toxin",
		"Viral",
		"Blast",
		"Gas",
		"Impact",
		"Puncture",
		"Slash",
		"Magazine",
		"Recoil",
		"Damage",
		"Range",
		"Slide",
		"Zoom",
	];

	/// <summary>Weapon Recoil is the one stat whose buff is printed with a minus sign.</summary>
	public static readonly FrozenSet<string> InvertedPolarityStats =
		new[] { "weapon recoil", "recoil" }.ToFrozenSet(StringComparer.Ordinal);

	/// <summary>Damage types that inherit the previous line's value on a combined-element card.</summary>
	public static readonly FrozenSet<string> DamageTypeStats = new[] {
		"impact", "puncture", "slash", "cold", "heat", "electricity", "toxin",
		"blast", "radiation", "gas", "magnetic", "viral", "corrosive",
	}.ToFrozenSet(StringComparer.Ordinal);

	private static readonly Dictionary<string, string> tagToDisplay = new(StringComparer.Ordinal) {
		["WeaponCritChanceMod"] = "Critical Chance",
		["WeaponCritDamageMod"] = "Critical Damage",
		["WeaponFireIterationsMod"] = "Multishot",
		["WeaponFireRateMod"] = "Fire Rate",
		["WeaponDamageAmountMod"] = "Damage",
		["WeaponMeleeDamageMod"] = "Melee Damage",
		["WeaponReloadSpeedMod"] = "Reload Speed",
		["WeaponStunChanceMod"] = "Status Chance",
		["WeaponProcTimeMod"] = "Status Duration",
		["WeaponPunctureDepthMod"] = "Punch Through",
		["WeaponClipMaxMod"] = "Magazine Capacity",
		["WeaponAmmoMaxMod"] = "Ammo Maximum",
		["WeaponRecoilReductionMod"] = "Weapon Recoil",
		["WeaponZoomFovMod"] = "Zoom",
		["WeaponProjectileSpeedMod"] = "Projectile Speed",
		["WeaponImpactDamageMod"] = "Impact",
		["WeaponArmorPiercingDamageMod"] = "Puncture",
		["WeaponSlashDamageMod"] = "Slash",
		["WeaponFreezeDamageMod"] = "Cold",
		["WeaponFireDamageMod"] = "Heat",
		["WeaponElectricityDamageMod"] = "Electricity",
		["WeaponToxinDamageMod"] = "Toxin",
		["WeaponFactionDamageGrineer"] = "Damage to Grineer",
		["WeaponFactionDamageCorpus"] = "Damage to Corpus",
		["WeaponFactionDamageInfested"] = "Damage to Infested",
		["WeaponMeleeFactionDamageGrineer"] = "Damage to Grineer",
		["WeaponMeleeFactionDamageCorpus"] = "Damage to Corpus",
		["WeaponMeleeFactionDamageInfested"] = "Damage to Infested",
		["WeaponMeleeRangeIncMod"] = "Range",
		["ComboDurationMod"] = "Combo Duration",
		["SlideAttackCritChanceMod"] = "Critical Chance for Slide Attack",
		["WeaponMeleeFinisherDamageMod"] = "Finisher Damage",
		["WeaponMeleeComboEfficiencyMod"] = "Heavy Attack Efficiency",
		["WeaponMeleeComboInitialBonusMod"] = "Initial Combo",
		["WeaponMeleeComboPointsOnHitMod"] = "Chance to Gain Combo Count",
		["WeaponMeleeComboBonusOnHitMod"] = "Additional Combo Count Chance",
	};

	private static readonly FrozenDictionary<string, string> displayToTag = BuildDisplayToTag();

	/// <summary>Tags the card prints as a flat number or factor instead of a percentage.</summary>
	public static readonly FrozenSet<string> NonPercentageTags = new[] {
		"WeaponFactionDamageGrineer",
		"WeaponFactionDamageCorpus",
		"WeaponFactionDamageInfested",
		"WeaponMeleeFactionDamageGrineer",
		"WeaponMeleeFactionDamageCorpus",
		"WeaponMeleeFactionDamageInfested",
		"WeaponMeleeComboInitialBonusMod",
		"ComboDurationMod",
		"WeaponMeleeRangeIncMod",
		// Metres, like melee range: the card reads "+3.7 Punch Through", no percent.
		"WeaponPunctureDepthMod",
	}.ToFrozenSet(StringComparer.Ordinal);

	/// <summary>Stats the card renders as a final factor (x1.05), so the 1 must be subtracted.</summary>
	public static bool IsMultiplierTag(string? tag)
	{
		if (tag == null || !NonPercentageTags.Contains(tag)) return false;
		return tag.Contains("FactionDamage", StringComparison.Ordinal) || tag == "WeaponMeleeComboInitialBonusMod";
	}

	public static bool IsFlatTag(string? tag) => tag != null && NonPercentageTags.Contains(tag) && !IsMultiplierTag(tag);

	private static FrozenDictionary<string, string> BuildDisplayToTag()
	{
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var (tag, display) in tagToDisplay) {
			map.TryAdd(display, tag);
		}

		// Labels the card uses that are not the canonical display name.
		map["Attack Speed"] = "WeaponFireRateMod";
		map["Slide"] = "SlideAttackCritChanceMod";
		map["Slide Attack"] = "SlideAttackCritChanceMod";
		map["Recoil"] = "WeaponRecoilReductionMod";
		map["Magazine"] = "WeaponClipMaxMod";
		map["Heavy Attack"] = "WeaponMeleeComboEfficiencyMod";
		map["Channeling Damage"] = "WeaponMeleeFinisherDamageMod";
		map["Channeling Efficiency"] = "WeaponMeleeComboEfficiencyMod";
		map["Flight Speed"] = "WeaponProjectileSpeedMod";
		return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
	}

	public static string? StatNameToTag(string statName)
		=> string.IsNullOrWhiteSpace(statName) ? null : displayToTag.TryGetValue(statName.Trim(), out var tag) ? tag : null;

	public static string TagToDisplayName(string tag, bool melee = false)
	{
		if (melee && tag == "WeaponFireRateMod") return "Attack Speed";
		return tagToDisplay.TryGetValue(tag, out var name) ? name : tag;
	}

	/// <summary>Riven type data lists both damage tags with identical bases; cards use one per class.</summary>
	public static string WeaponDamageTag(string tag, bool melee)
	{
		if (melee && tag == "WeaponDamageAmountMod") return "WeaponMeleeDamageMod";
		if (!melee && tag == "WeaponMeleeDamageMod") return "WeaponDamageAmountMod";
		return tag;
	}

	/// <summary>Turns "|val|% &lt;DT_FIRE_COLOR&gt;Heat" into "Heat".</summary>
	public static string CleanLocTag(string raw)
	{
		if (string.IsNullOrEmpty(raw)) return "";

		var cleaned = ValuePlaceholder().Replace(raw, "");
		cleaned = ColourTag().Replace(cleaned, "");
		cleaned = Parenthetical().Replace(cleaned, " ");
		return WhitespaceRun().Replace(cleaned, " ").Trim();
	}

	[GeneratedRegex(@"\|[^|]*\|[%s]?\s*")]
	private static partial Regex ValuePlaceholder();

	[GeneratedRegex("<[^>]+>")]
	private static partial Regex ColourTag();

	[GeneratedRegex(@"\s*\(.*?\)\s*")]
	private static partial Regex Parenthetical();

	[GeneratedRegex(@"\s+")]
	private static partial Regex WhitespaceRun();
}
