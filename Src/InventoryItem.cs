using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace framenion.Src;

/// <summary>
/// One priced row on the inventory tab: a stack the player owns, resolved against the
/// warframe.market catalogue. <see cref="Slug"/> is the handle the market tools will list it
/// under, and is empty for anything the market does not carry.
/// </summary>
public sealed class InventoryItem
{
	/// <summary>Ducats per platinum above which a part is clearly better spent at Baro's kiosk.</summary>
	private const double goodRatio = 10.0;
	private const double fairRatio = 6.0;

	private const string placeholder = "—";

	public required string Name { get; init; }

	/// <summary>The inventory dump's uniqueName. Also the market's gameRef, which is how the two are matched.</summary>
	public required string ItemType { get; init; }

	public required string Category { get; init; }

	/// <summary>warframe.market slug, or empty when the market does not list this item.</summary>
	public string Slug { get; init; } = "";

	public string IconPath { get; init; } = "";

	/// <summary>Relic refinement ("Intact" through "Radiant"), empty for everything else.</summary>
	public string Refinement { get; init; } = "";

	public int OwnedCount { get; init; }

	/// <summary>Average market price, or 0 when no quote is available.</summary>
	public double Platinum { get; init; }

	public int Ducats { get; init; }

	/// <summary>Units traded on the market today, a rough liquidity hint. 0 when unknown.</summary>
	public int Volume { get; init; }

	/// <summary>
	/// False when <see cref="Platinum"/> is a seller's asking price rather than a completed
	/// trade, which is what thin markets fall back to and reads optimistically high.
	/// </summary>
	public bool FromClosedOrders { get; init; }

	public bool IsPrime { get; init; }

	/// <summary>
	/// Whether the equipment this part builds is already mastered, so the copies are free to
	/// sell. Null when nothing masterable needs the item at all, which is every relic, mod and
	/// arcane.
	/// </summary>
	public bool? ParentMastered { get; init; }

	// Decoded on first binding read, like the other tile models: the list is thousands of rows
	// and only a screenful is ever realized.
	public Bitmap? Icon => GameData.GetOrCreateBitmap(IconPath, 48);

	public bool HasPlatinum => Platinum > 0;

	public bool HasDucats => Ducats > 0;

	public bool HasRatio => DucatsPerPlatinum != null;

	public double? DucatsPerPlatinum => Ducats > 0 && Platinum > 0 ? Ducats / Platinum : null;

	/// <summary>What the whole stack is worth, which is what makes a row worth acting on.</summary>
	public double TotalPlatinum => Platinum * OwnedCount;

	public string DisplayName => Refinement.Length > 0 ? $"{Name} ({Refinement})" : Name;

	public string CountText => OwnedCount.ToString("N0", CultureInfo.InvariantCulture);

	public string QuantityText => $"x{CountText}";

	public string PlatinumText => Platinum > 0 ? Platinum.ToString("0.#", CultureInfo.InvariantCulture) : placeholder;

	public string DucatsText => Ducats > 0 ? Ducats.ToString(CultureInfo.InvariantCulture) : placeholder;

	public string RatioText => DucatsPerPlatinum is { } ratio ? ratio.ToString("0.0", CultureInfo.InvariantCulture) : placeholder;

	public string TotalPlatinumText => TotalPlatinum > 0 ? TotalPlatinum.ToString("N0", CultureInfo.InvariantCulture) : placeholder;

	public string VolumeText => Volume > 0 ? Volume.ToString("N0", CultureInfo.InvariantCulture) : placeholder;

	/// <summary>Greens the ratio once ducating beats selling, so the column can be skimmed.</summary>
	public string RatioColor => DucatsPerPlatinum switch {
		>= goodRatio => "#3ABA29",
		>= fairRatio => "#FFD700",
		not null => "#D0D0D0",
		_ => "#6A6A6A",
	};

	public string PlatinumColor => Platinum switch {
		> 0 when FromClosedOrders => "#FFFFFF",
		// Dimmed so a row priced off asking orders is not read as a realized price.
		> 0 => "#A89A6B",
		_ => "#6A6A6A",
	};

	public string PlatinumTooltip => Platinum switch {
		<= 0 => "No market price available",
		_ when FromClosedOrders => $"Median of trades that closed. {VolumeText} traded today.",
		_ => $"No completed trades; this is the median asking price. {VolumeText} traded today.",
	};

	/// <summary>
	/// The tile only has room for the headline numbers, so the rest of what the row knows is on
	/// hover: where the price came from and what the whole stack is worth.
	/// </summary>
	public string DetailsTooltip
	{
		get {
			var lines = new List<string> {
				DisplayName,
				$"{Category}  ·  {CountText} owned",
				$"Platinum: {PlatinumText}   Ducats: {DucatsText}",
			};

			if (HasRatio) lines.Add($"{RatioText} ducats per platinum");
			if (TotalPlatinum > 0) lines.Add($"Stack value: {TotalPlatinumText} plat");
			if (ParentMastered == true) lines.Add("Already mastered — safe to sell");
			lines.Add(PlatinumTooltip);

			return string.Join(Environment.NewLine, lines);
		}
	}
}
