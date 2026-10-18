using Avalonia.Media.Imaging;
using System.Collections.Generic;

namespace framenion.Src.Riven;

/// <summary>One riven mod from the player's inventory, graded and ready to bind.</summary>
public sealed class OwnedRiven
{
	public required string Id { get; init; }

	/// <summary>The weapon the riven fits, from the fingerprint's compat path.</summary>
	public required string WeaponName { get; init; }

	/// <summary>The name the game gives the riven, rebuilt from its stat syllables.</summary>
	public required string RivenName { get; init; }

	/// <summary>"Rifle Riven Mod", "Zaw Riven Mod", and so on. Searchable but not shown.</summary>
	public required string ClassName { get; init; }

	/// <summary>Filter tag: Rifle, Shotgun, Pistol, Melee, Archgun, Modular, Companion.</summary>
	public required string Category { get; init; }

	public required string IconPath { get; init; }
	public required double Disposition { get; init; }

	/// <summary>Mastery rank the riven demands, from the fingerprint's lvlReq.</summary>
	public required int MasteryRank { get; init; }

	public required int Rerolls { get; init; }
	public required string Polarity { get; init; }

	/// <summary>An unveiled riven has no stats yet, so there is nothing to grade.</summary>
	public required bool Veiled { get; init; }

	public required string OverallGrade { get; init; }

	/// <summary>Mean roll position across the graded stats, 0..1.</summary>
	public required double StatPerfectness { get; init; }

	public required IReadOnlyList<GradedRivenStat> Stats { get; init; }

	public Bitmap? Icon => GameData.GetOrCreateBitmap(IconPath);

	public bool HasGrade => !Veiled && OverallGrade.Length > 0;

	/// <summary>Falls back to the riven's class when the name could not be rebuilt.</summary>
	public string Subtitle => Veiled
		? "Veiled"
		: $"{(RivenName.Length > 0 ? RivenName : ClassName)}  ·  dispo {Disposition:0.00}";

	public string Footer => Veiled
		? $"MR {MasteryRank}"
		: $"MR {MasteryRank}  ·  {Rerolls} roll{(Rerolls == 1 ? "" : "s")}  ·  {Polarity}  ·  {StatPerfectness * 100:0}%";
}
