using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion.Src;

public sealed class InventorySummary
{
	public string Platinum { get; init; } = "";
	public string Credits { get; init; } = "";
	public string Mastery { get; init; } = "";
	public string MasteryIconPath { get; init; } = "";
	public string PlayerIconPath { get; init; } = "";
}

/// <summary>
/// Reads the inventory dump produced by warframe-api-helper and projects it onto the catalogue:
/// owned counts, mastery state and the tile border colours.
/// </summary>
public static class Inventory
{
	private const string textIconsUrl = "https://raw.githubusercontent.com/calamity-inc/warframe-public-export-plus/refs/heads/senpai/ExportTextIcons.json";
	private static readonly TimeSpan textIconsMaxAge = TimeSpan.FromDays(7);

	private const string ownedBorder = "#3aba29";
	private const string craftableBorder = "#4A9EFF";
	private const string fromRelicBorder = "#FFD700";
	private const string defaultBorder = "#4A4A4A";

	public static string FilePath => Path.Combine(AppData.AppDataDir, "inventory.json");

	public static async Task<InventorySummary?> LoadAsync()
	{
		if (!File.Exists(FilePath)) return null;

		await using var stream = File.OpenRead(FilePath);
		using var doc = await JsonDocument.ParseAsync(stream);
		var root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object) return null;

		string platinum = ReadCurrency(root, "PremiumCredits");
		string credits = ReadCurrency(root, "RegularCredits");

		int rank = root.TryGetProperty("PlayerLevel", out var levelEl) && levelEl.ValueKind == JsonValueKind.Number && levelEl.TryGetInt32(out var level) ? level : 0;
		string mastery = rank > 30 ? $"L{rank - 30}" : rank.ToString();

		string masteryIconPath = await ResolveRankIconAsync(rank);
		string playerIconPath = await ResolvePlayerIconAsync(root);

		ApplyToCatalog(root);
		GameData.RivenList = await Riven.RivenInventory.BuildAsync(root);
		GameData.InventoryList = await InventoryCatalog.BuildAsync(root);

		return new InventorySummary {
			Platinum = platinum,
			Credits = credits,
			Mastery = mastery,
			MasteryIconPath = masteryIconPath,
			PlayerIconPath = playerIconPath,
		};
	}

	private static string ReadCurrency(JsonElement root, string property)
		=> root.TryGetProperty(property, out var el) && el.TryGetInt64(out var value) ? value.ToString("N0") : "";

	private static async Task<string> ResolveRankIconAsync(int rank)
	{
		try {
			var cached = await GetTextIconsAsync();
			if (cached == null) return "";

			using var doc = JsonDocument.Parse(cached);
			if (!doc.RootElement.TryGetProperty($"RANK_{rank}", out var entry)) return "";
			if (!entry.TryGetProperty("DIT_AUTO", out var iconEl) || iconEl.GetString() is not string icon || icon.Length == 0) return "";

			await GameData.DownloadIconAsync($"https://browse.wf{icon}");
			return GameData.GetLocalIconPath(icon);
		} catch (Exception ex) {
			Log.Warn("Could not resolve the mastery rank icon.", ex);
			return "";
		}
	}

	/// <summary>Caches the rank icon table on disk; it used to be re-downloaded on every refresh.</summary>
	private static async Task<byte[]?> GetTextIconsAsync()
	{
		var path = Path.Combine(AppData.CacheDir, "ExportTextIcons.json");
		var info = new FileInfo(path);
		if (info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc < textIconsMaxAge) {
			return await File.ReadAllBytesAsync(path);
		}

		try {
			await AppData.DownloadToFileAsync(textIconsUrl, path);
		} catch (Exception ex) {
			Log.Warn("Could not refresh the rank icon table.", ex);
		}
		return File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
	}

	private static async Task<string> ResolvePlayerIconAsync(JsonElement root)
	{
		try {
			if (!root.TryGetProperty("ActiveAvatarImageType", out var avatarEl)) return "";
			if (avatarEl.GetString() is not string avatar || avatar.Length == 0) return "";
			if (!GameData.ExportManifest.TryGetValue(avatar, out var icon) || string.IsNullOrEmpty(icon)) return "";

			await GameData.DownloadIconAsync(icon);
			return GameData.GetLocalIconPath(icon);
		} catch (Exception ex) {
			Log.Warn("Could not resolve the player glyph.", ex);
			return "";
		}
	}

	private static void ApplyToCatalog(JsonElement root)
	{
		if (!root.TryGetProperty("MiscItems", out var miscEl) || !root.TryGetProperty("Recipes", out var recipesEl) || !root.TryGetProperty("XPInfo", out var xpInfoEl)) {
			Log.Warn("Inventory dump is missing MiscItems/Recipes/XPInfo; skipping catalogue projection.");
			return;
		}

		var xpByType = ReadLongMap(xpInfoEl, "XP");
		var miscByType = ReadIntMap(miscEl);
		var recipesByType = ReadIntMap(recipesEl);
		var ownedTypes = ReadOwnedTypes(root);

		foreach (var relic in GameData.RelicList) {
			relic.OwnedCount = miscByType.TryGetValue(relic.Type, out var count) ? count : 0;
		}

		// One pass over owned relics instead of re-scanning the relic list for every prime
		// ingredient of every item.
		var ownedRewardNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var relic in GameData.RelicList) {
			if (relic.OwnedCount <= 0) continue;
			foreach (var reward in relic.Rewards) {
				ownedRewardNames.Add(reward.Name);
			}
		}

		foreach (var item in GameData.ItemsList) {
			item.Mastered = xpByType.TryGetValue(item.Type, out var xp) && xp >= GameData.XpToMaster(item.Type);
			item.BorderColor = ownedTypes.Contains(item.Type) ? ownedBorder : defaultBorder;

			foreach (var ingredient in item.Ingredients) {
				ingredient.OwnedCount = 0;
				ingredient.BorderColor = defaultBorder;

				if (!miscByType.TryGetValue(ingredient.ItemType, out var owned) && !recipesByType.TryGetValue(ingredient.RecipeKey, out owned)) {
					if (ownedRewardNames.Contains(ingredient.Name)) {
						ingredient.BorderColor = fromRelicBorder;
					}
					continue;
				}

				ingredient.OwnedCount = owned;
				if (owned >= ingredient.Count) continue;
				if (!GameData.ExportRecipes.TryGetValue(ingredient.ItemType, out var subRecipe)) continue;

				bool canCraft = true;
				foreach (var sub in subRecipe.Ingredients) {
					if (!miscByType.TryGetValue(sub.Type, out var subOwned) || subOwned < sub.Count) {
						canCraft = false;
						break;
					}
				}
				if (canCraft) ingredient.BorderColor = craftableBorder;
			}

			if (item.Ingredients.Count > 0 && AllOwned(item)) item.BorderColor = craftableBorder;
		}
	}

	private static bool AllOwned(Item item)
	{
		foreach (var ingredient in item.Ingredients) {
			if (ingredient.OwnedCount < ingredient.Count) return false;
		}
		return true;
	}

	private static HashSet<string> ReadOwnedTypes(JsonElement root)
	{
		string[] categories = [
			"Suits", "MechSuits", "SpaceSuits", "LongGuns", "Pistols", "Melee",
			"SpaceGuns", "SpaceMelee", "Sentinels", "SentinelWeapons", "KubrowPets",
		];

		var owned = new HashSet<string>(StringComparer.Ordinal);
		foreach (var category in categories) {
			if (!root.TryGetProperty(category, out var array) || array.ValueKind != JsonValueKind.Array) continue;
			foreach (var entry in array.EnumerateArray()) {
				if (entry.TryGetProperty("ItemType", out var t) && t.GetString() is string type) owned.Add(type);
			}
		}
		return owned;
	}

	private static Dictionary<string, int> ReadIntMap(JsonElement array)
	{
		var map = new Dictionary<string, int>(StringComparer.Ordinal);
		if (array.ValueKind != JsonValueKind.Array) return map;

		foreach (var entry in array.EnumerateArray()) {
			if (entry.TryGetProperty("ItemType", out var t) && t.GetString() is string type
				&& entry.TryGetProperty("ItemCount", out var c) && c.TryGetInt32(out var count)) {
				map[type] = count;
			}
		}
		return map;
	}

	private static Dictionary<string, long> ReadLongMap(JsonElement array, string valueProperty)
	{
		var map = new Dictionary<string, long>(StringComparer.Ordinal);
		if (array.ValueKind != JsonValueKind.Array) return map;

		foreach (var entry in array.EnumerateArray()) {
			if (entry.TryGetProperty("ItemType", out var t) && t.GetString() is string type
				&& entry.TryGetProperty(valueProperty, out var v) && v.TryGetInt64(out var value)) {
				map[type] = value;
			}
		}
		return map;
	}
}
