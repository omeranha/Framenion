using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion.Src;

/// <summary>
/// Flattens the inventory dump's countable buckets into one priced list, the rows the inventory
/// tab shows. Equipment, rivens and relic stock are projected elsewhere; this is the tradeable
/// stock view the market tools work from, so every row carries the slug it would be listed under.
/// </summary>
internal static class InventoryCatalog
{
	/// <summary>
	/// Buckets that hold stacks with an ItemCount. Ranked mod and arcane instances live in
	/// "Upgrades" instead, one object per copy with its own rank, and are left out: the market
	/// quotes them per rank, so folding them in here would misprice them.
	/// </summary>
	private static readonly string[] stackBuckets = ["MiscItems", "Recipes", "RawUpgrades"];

	/// <summary>
	/// What the tab carries: the goods that accumulate and then need a decision, sell or ducat
	/// or crack. Components and blueprints are the item parts, down to every chassis, neuroptics
	/// and link. Crafting resources, scenes, focus lenses and Ayatan stars are deliberately out.
	/// </summary>
	private static readonly FrozenSet<string> includedCategories =
		new[] { "Component", "Blueprint", "Relic", "Mod", "Arcane" }.ToFrozenSet(StringComparer.Ordinal);

	public static async Task<List<InventoryItem>> BuildAsync(JsonElement root)
	{
		var counts = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var bucket in stackBuckets) {
			if (!root.TryGetProperty(bucket, out var array) || array.ValueKind != JsonValueKind.Array) continue;

			foreach (var entry in array.EnumerateArray()) {
				if (entry.TryGetProperty("ItemType", out var typeEl) && typeEl.GetString() is string type && type.Length > 0
					&& entry.TryGetProperty("ItemCount", out var countEl) && countEl.TryGetInt32(out int count) && count > 0) {
					// A type can show up in more than one bucket; the stacks are the same holding.
					counts[type] = counts.TryGetValue(type, out int running) ? running + count : count;
				}
			}
		}

		var rows = new List<InventoryItem>(counts.Count);
		var iconUrls = new HashSet<string>(StringComparer.Ordinal);

		foreach (var (type, count) in counts) {
			// Relics are listed once per relic on the market, refinement aside, so the metal
			// suffix comes off before the lookup and is kept for the label.
			var (lookupType, refinement) = GameData.SplitRelicRefinement(type);

			// Anything the market does not carry has no price, no ducat value and nothing to
			// decide, so it never becomes a row.
			if (!GameData.WFMarketByGameRef.TryGetValue(lookupType, out var market)) continue;
			if (!includedCategories.Contains(market.Category)) continue;

			var icon = ResolveIcon(type);
			if (icon.Length > 0) iconUrls.Add(icon);

			rows.Add(new InventoryItem {
				Name = market.Name,
				ItemType = type,
				Category = market.Category,
				Slug = market.Slug,
				IconPath = GameData.GetLocalIconPath(icon),
				Refinement = refinement,
				OwnedCount = count,
				Platinum = market.Platinum,
				Ducats = market.Ducats > 0 ? market.Ducats : ResolveExportDucats(market.Name),
				Volume = market.Volume,
				FromClosedOrders = market.FromClosedOrders,
				IsPrime = market.IsPrime,
				ParentMastered = ResolveParentMastered(market.Name, market.Category),
			});
		}

		// Mods and arcanes are not part of the equipment catalogue, so their icons are only
		// fetched here. Already-cached files are skipped, which is every run after the first.
		await GameData.DownloadIconsAsync(iconUrls);

		rows.Sort((a, b) => {
			int value = b.TotalPlatinum.CompareTo(a.TotalPlatinum);
			return value != 0 ? value : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
		});

		Log.Info($"Inventory rows built: {rows.Count}.");
		return rows;
	}

	/// <summary>
	/// The manifest carries a texture for nearly everything the dump can contain; the catalogue
	/// and the recipe's result cover the handful it misses.
	/// </summary>
	private static string ResolveIcon(string itemType)
	{
		if (GameData.ExportManifest.TryGetValue(itemType, out var texture) && texture.Length > 0) return texture;
		if (GameData.Catalog.TryGetValue(itemType, out var entry) && entry.Icon.Length > 0) return entry.Icon;
		if (GameData.ExportRelics.TryGetValue(itemType, out var relic) && relic.Icon.Length > 0) return relic.Icon;

		return GameData.RecipesByUniqueName.TryGetValue(itemType, out var recipe)
			&& GameData.Catalog.TryGetValue(recipe.ResultType, out var result)
			? result.Icon
			: "";
	}

	/// <summary>Covers parts the market has not published a ducat value for yet.</summary>
	private static int ResolveExportDucats(string name)
		=> GameData.DucatsByName.TryGetValue(name, out int ducats) ? ducats : 0;

	/// <summary>
	/// Finds the equipment a part builds and reports whether it is mastered, which is what makes
	/// spare copies free to sell. Relics, mods and arcanes build nothing, so they report null and
	/// drop out of the mastery filter rather than pretending to an answer.
	/// </summary>
	private static bool? ResolveParentMastered(string name, string category)
	{
		if (category != "Component" && category != "Blueprint") return null;

		// The index carries both spellings a part goes by, the crafted component and its
		// blueprint, so either name the market uses resolves.
		return GameData.ItemsByIngredientName.TryGetValue(name, out var parent) ? parent.Mastered : null;
	}
}
