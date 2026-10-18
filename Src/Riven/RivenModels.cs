using System.Collections.Generic;

namespace framenion.Src.Riven;

/// <summary>One stat as read off the card. Value is unsigned; sign lives in <see cref="Positive"/>.</summary>
public sealed class RivenStat
{
	public required string Name { get; set; }

	/// <summary>Effective polarity (buff or curse), after the Weapon Recoil inversion.</summary>
	public required bool Positive { get; set; }

	/// <summary>Set only when the card's printed sign differs from the effective polarity.</summary>
	public bool? DisplayPositive { get; set; }

	/// <summary>Unsigned percent/flat value, or the raw factor when <see cref="Multiplier"/>.</summary>
	public double? Value { get; set; }

	/// <summary>True for faction-damage style stats the card prints as "x1.25".</summary>
	public bool Multiplier { get; set; }

	public RivenStat Clone() => new() {
		Name = Name,
		Positive = Positive,
		DisplayPositive = DisplayPositive,
		Value = Value,
		Multiplier = Multiplier,
	};
}

public sealed class GradedRivenStat
{
	public required string Name { get; init; }
	public required bool Positive { get; init; }
	public required double? Value { get; init; }
	public required bool Multiplier { get; init; }

	/// <summary>Letter grade, "?" when the value could not be graded.</summary>
	public required string Grade { get; init; }

	/// <summary>Position within the stat's possible range, 0..1.</summary>
	public required double RollFloat { get; init; }

	/// <summary>The sign the card prints, which differs from polarity for Weapon Recoil.</summary>
	public bool DisplaySign => DisplayPositive ?? Positive;

	public bool? DisplayPositive { get; init; }

	public string DisplayValue
	{
		get {
			if (Value == null) return "?";
			if (Multiplier) return $"x{Value.Value:0.00}";
			string sign = DisplaySign ? "+" : "-";
			return RivenStatTables.IsFlatTag(RivenStatTables.StatNameToTag(Name))
				? $"{sign}{Value.Value:0.##}"
				: $"{sign}{Value.Value:0.#}%";
		}
	}
}

public sealed record RivenUpgradeEntry(string Tag, double BaseValue, string DisplayName, string Prefix = "", string Suffix = "");

/// <summary>A rectangle in captured-frame pixels.</summary>
public readonly record struct PixelBounds(int X, int Y, int Width, int Height)
{
	public bool IsEmpty => Width <= 0 || Height <= 0;
}

public sealed record RivenModType(string UniqueName, string Name, IReadOnlyList<RivenUpgradeEntry> Entries);

public sealed record RivenWeapon(string Name, string UniqueName, double Disposition, string Category, double Multishot);

public sealed class RivenGradeResult
{
	public required IReadOnlyList<GradedRivenStat> Stats { get; init; }
	public required string OverallGrade { get; init; }

	/// <summary>Mean roll position across graded stats; higher is closer to a perfect roll.</summary>
	public required double StatPerfectness { get; init; }

	public required double Disposition { get; init; }
	public required int AssumedRank { get; init; }
	public required string RivenTypeName { get; init; }
}

public sealed class RivenScanResult
{
	public string WeaponName { get; set; } = "";
	public required IReadOnlyList<RivenStat> Stats { get; init; }
	public RivenGradeResult? Grade { get; set; }
	public string RawText { get; init; } = "";

	/// <summary>Where the card was read, in frame pixels, so the overlay can sit beside it.</summary>
	public PixelBounds Card { get; set; }

	/// <summary>True when the read looks short or malformed and should be rescanned.</summary>
	public bool Incomplete { get; init; }
}
