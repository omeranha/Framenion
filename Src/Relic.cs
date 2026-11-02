using Avalonia.Media.Imaging;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace framenion.Src;

public class Relic(string name, string type, string iconPath, ObservableCollection<Reward> rewards)
{
	public string Name { get; set; } = name;
	public string Type { get; set; } = type;
	public string IconPath { get; set; } = iconPath;

	public Bitmap? Icon => GameData.GetOrCreateBitmap(IconPath);

	public string Era { get; set; } = "";
	public string Quality { get; set; } = "";
	public ObservableCollection<Reward> Rewards { get; set; } = rewards;
	public int OwnedCount { get; set; } = 0;
	public bool IsCountVisible => OwnedCount > 0;
	public bool Unowned => OwnedCount == 0;
}

public class Reward(string name, string type, string iconPath, int count, string rarity, string borderColor)
{
	public string Name { get; set; } = name;
	public string Type { get; set; } = type;
	public string IconPath { get; set; } = iconPath;

	public Bitmap? Icon => GameData.GetOrCreateBitmap(IconPath);

	public int Count { get; set; } = count;
	public string Rarity { get; set; } = rarity;
	public string BorderColor { get; set; } = borderColor;

	public string CountName => Count > 1 ? $"{Count}x {Name}" : Name;
}

/// <summary>
/// One relic with all of its refinements behind it. The export lists Intact, Exceptional,
/// Flawless and Radiant as four separate entries that share a name, an icon and a reward table,
/// so binding the panel straight to those produced four identical tiles per relic. The counts
/// read through to the underlying entries, so an inventory refresh needs no rebuild here.
/// </summary>
public class RelicGroup(string name, string era, string iconPath, ObservableCollection<Reward> rewards, IReadOnlyList<Relic> refinements)
{
	public string Name { get; } = name;
	public string Era { get; } = era;
	public string IconPath { get; } = iconPath;
	public ObservableCollection<Reward> Rewards { get; } = rewards;

	/// <summary>The per-refinement entries, in refinement order.</summary>
	public IReadOnlyList<Relic> Refinements { get; } = refinements;

	public Bitmap? Icon => GameData.GetOrCreateBitmap(IconPath);

	public int OwnedCount
	{
		get {
			int total = 0;
			foreach (var refinement in Refinements) total += refinement.OwnedCount;
			return total;
		}
	}

	public bool IsCountVisible => OwnedCount > 0;
	public bool Unowned => OwnedCount == 0;

	/// <summary>Only the refinements actually owned, e.g. "3 Intact · 1 Radiant".</summary>
	public string RefinementSummary
	{
		get {
			var owned = new List<string>();
			foreach (var refinement in Refinements) {
				if (refinement.OwnedCount > 0) owned.Add($"{refinement.OwnedCount} {refinement.Quality}");
			}
			return string.Join(" · ", owned);
		}
	}

	/// <summary>Highest refinement owned as a sort key, 0 when the relic is unowned.</summary>
	public int BestOwnedRefinement
	{
		get {
			int best = 0;
			foreach (var refinement in Refinements) {
				if (refinement.OwnedCount <= 0) continue;
				int key = GameData.GetRelicSortKey(refinement.Quality);
				if (key != int.MaxValue && key > best) best = key;
			}
			return best;
		}
	}

	/// <summary>Full breakdown including the refinements owned none of, which the summary hides.</summary>
	public string DetailsTooltip
	{
		get {
			var lines = new List<string> { Name };
			foreach (var refinement in Refinements) {
				lines.Add($"{refinement.Quality}: {refinement.OwnedCount}");
			}
			return string.Join("\n", lines);
		}
	}
}
