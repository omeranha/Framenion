using Avalonia.Media.Imaging;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

public class ItemData(string slug, string ducats, string price, string volume)
{
	public string Slug { get; set; } = slug;
	public string Ducats { get; set; } = ducats;
	public string Price { get; set; } = price;
	public string Volume { get; set; } = volume;
}

/// <summary>A display name, icon and ducat value resolved from the export for one uniqueName.</summary>
public sealed record CatalogEntry(string Name, string Icon, int Ducats);

public static class GameData
{
	public static readonly IReadOnlyDictionary<string, (string name, string color)> relicType = new Dictionary<string, (string, string)>(StringComparer.Ordinal) {
		["VoidT1"] = ("Lith", "#72523c"),
		["VoidT2"] = ("Meso", "#917147"),
		["VoidT3"] = ("Neo", "#c9c3c4"),
		["VoidT4"] = ("Axi", "#FFD700"),
		["VoidT5"] = ("Requiem", "#e80c1e"),
		["VoidT6"] = ("Omnia", "#FFFFFF")
	};

	// Relic refinement is stored as a metal tier in the uniqueName.
	private static readonly (string Suffix, string Quality)[] relicQualities = [
		("Bronze", "Intact"),
		("Silver", "Exceptional"),
		("Gold", "Flawless"),
		("Platinum", "Radiant"),
	];

	public static List<VoidFissure> Fissures { get; set; } = [];
	public static List<Item> ItemsList { get; set; } = [];
	public static List<Relic> RelicList { get; set; } = [];

	/// <summary>Owned riven mods, read from the inventory dump rather than the export.</summary>
	public static List<Riven.OwnedRiven> RivenList { get; set; } = [];

	#region Raw export
	public static FrozenDictionary<string, string> ExportManifest { get; set; } = FrozenDictionary<string, string>.Empty;
	public static FrozenDictionary<string, RegionDTO> ExportRegions { get; set; } = FrozenDictionary<string, RegionDTO>.Empty;
	public static FrozenDictionary<string, RecipeDTO> ExportRecipes { get; set; } = FrozenDictionary<string, RecipeDTO>.Empty;
	public static FrozenDictionary<string, RecipeDTO> RecipesByUniqueName { get; set; } = FrozenDictionary<string, RecipeDTO>.Empty;
	public static FrozenDictionary<string, ResourceDTO> ExportResources { get; set; } = FrozenDictionary<string, ResourceDTO>.Empty;
	public static FrozenDictionary<string, ItemDTO> ExportWarframes { get; set; } = FrozenDictionary<string, ItemDTO>.Empty;
	public static FrozenDictionary<string, ItemDTO> ExportWeapons { get; set; } = FrozenDictionary<string, ItemDTO>.Empty;
	public static FrozenDictionary<string, ItemDTO> ExportSentinels { get; set; } = FrozenDictionary<string, ItemDTO>.Empty;
	public static FrozenDictionary<string, RelicDTO> ExportRelics { get; set; } = FrozenDictionary<string, RelicDTO>.Empty;

	/// <summary>Riven stat pools, keyed by the randomized upgrade's uniqueName.</summary>
	public static FrozenDictionary<string, Riven.RivenModType> RivenMods { get; set; } = FrozenDictionary<string, Riven.RivenModType>.Empty;
	#endregion

	#region Derived indexes
	/// <summary>uniqueName to display name, icon and ducat value, across every catalogue file.</summary>
	public static FrozenDictionary<string, CatalogEntry> Catalog { get; private set; } = FrozenDictionary<string, CatalogEntry>.Empty;

	/// <summary>Display name to ducat value, for the reward overlay and ingredient tooltips.</summary>
	public static FrozenDictionary<string, int> DucatsByName { get; private set; } = FrozenDictionary<string, int>.Empty;

	/// <summary>Every display name obtainable from a relic. This is the OCR match pool.</summary>
	public static IReadOnlyList<string> RewardNames { get; private set; } = [];

	/// <summary>Ingredient display name to the item that needs it, for the reward overlay.</summary>
	public static FrozenDictionary<string, Item> ItemsByIngredientName { get; private set; } = FrozenDictionary<string, Item>.Empty;

	/// <summary>Equipment whose mastery rank caps at 40 instead of 30.</summary>
	public static FrozenSet<string> Level40Types { get; private set; } = FrozenSet<string>.Empty;

	public static FrozenDictionary<string, ItemData> WFMarketData { get; set; } = FrozenDictionary<string, ItemData>.Empty;
	#endregion

	#region Bitmaps
	public static Bitmap? GetOrCreateBitmap(string localPath, int decodeWidth = 80)
	{
		if (string.IsNullOrEmpty(localPath)) return null;

		var lazy = AppData.BitmapCache.GetOrAdd(localPath, path =>
			new Lazy<Bitmap?>(() => {
				try {
					if (!File.Exists(path)) return null;
					using var fs = File.OpenRead(path);
					return Bitmap.DecodeToWidth(fs, decodeWidth, BitmapInterpolationMode.LowQuality);
				} catch (Exception ex) {
					Log.Warn($"Could not decode icon '{path}'.", ex);
					return null;
				}
			}, LazyThreadSafetyMode.ExecutionAndPublication));
		return lazy.Value;
	}

	public static void ClearBitmapCache()
	{
		foreach (var kv in AppData.BitmapCache) {
			if (kv.Value.IsValueCreated && kv.Value.Value is IDisposable d) {
				try { d.Dispose(); } catch { /* best effort */ }
			}
		}
		AppData.BitmapCache.Clear();
	}

	/// <summary>
	/// Maps a texture location to its cache file. The export appends a content hash to every
	/// texture name, so the leaf name is already unique and self-versioning.
	/// </summary>
	public static string GetLocalIconPath(string icon)
	{
		if (string.IsNullOrEmpty(icon)) return "";

		foreach (var marker in (string[])["/CraftingComponents/", "/AvatarImages/", "/Relics/"]) {
			int index = icon.IndexOf(marker, StringComparison.Ordinal);
			if (index >= 0) {
				return Path.Combine(AppData.IconsCacheDir, icon[(index + marker.Length)..]);
			}
		}

		int slash = icon.LastIndexOf('/');
		return Path.Combine(AppData.IconsCacheDir, slash >= 0 ? icon[(slash + 1)..] : icon);
	}

	public static async Task DownloadIconAsync(string icon)
	{
		var iconPath = GetLocalIconPath(icon);
		if (string.IsNullOrEmpty(iconPath) || File.Exists(iconPath)) return;

		await AppData.IconDownloadSemaphore.WaitAsync();
		try {
			if (File.Exists(iconPath)) return;

			Directory.CreateDirectory(AppData.IconsCacheDir);
			// Write to a temp file first: an interrupted transfer would otherwise leave a
			// truncated PNG that File.Exists accepts forever.
			var temp = iconPath + ".part";
			try {
				using (var source = await AppData.GetStreamAsync(icon))
				await using (var file = File.Create(temp)) {
					await source.CopyToAsync(file);
				}
				File.Move(temp, iconPath, overwrite: true);
			} finally {
				if (File.Exists(temp)) {
					try { File.Delete(temp); } catch { /* best effort */ }
				}
			}
		} catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) {
			Log.Debug($"Icon not published: {icon}");
		} catch (Exception ex) {
			Log.Warn($"Could not download icon '{icon}'.", ex);
		} finally {
			AppData.IconDownloadSemaphore.Release();
		}
	}

	public static async Task DownloadIconsAsync(IEnumerable<string> icons)
	{
		var pending = icons.Where(i => !string.IsNullOrEmpty(i)).Distinct(StringComparer.Ordinal).ToList();
		if (pending.Count == 0) return;

		Log.Info($"Resolving {pending.Count} icon(s).");
		await Parallel.ForEachAsync(pending, async (icon, _) => await DownloadIconAsync(icon));
	}
	#endregion

	/// <summary>
	/// Builds the lookup tables every other feature reads from. Must run after the export files
	/// have been parsed and before the catalogue is built.
	/// </summary>
	public static void BuildIndexes()
	{
		var catalog = new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);

		foreach (var (key, resource) in ExportResources) {
			if (string.IsNullOrEmpty(resource.Name)) continue;
			catalog[key] = new CatalogEntry(CleanDisplayName(resource.Name), resource.Icon, resource.Ducats ?? 0);
		}

		foreach (var source in (IEnumerable<KeyValuePair<string, ItemDTO>>[])[ExportWarframes, ExportWeapons, ExportSentinels]) {
			foreach (var (key, item) in source) {
				if (string.IsNullOrEmpty(item.Name)) continue;
				catalog[key] = new CatalogEntry(CleanDisplayName(item.Name), item.Icon, 0);
			}
		}

		Catalog = catalog.ToFrozenDictionary(StringComparer.Ordinal);

		var ducats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		foreach (var entry in catalog.Values) {
			if (entry.Ducats > 0) ducats[entry.Name] = entry.Ducats;
		}
		foreach (var recipe in RecipesByUniqueName.Values) {
			if (recipe.Ducats is not > 0) continue;
			if (!catalog.TryGetValue(recipe.ResultType, out var result)) continue;
			ducats[$"{result.Name} Blueprint"] = recipe.Ducats.Value;
		}
		DucatsByName = ducats.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

		Level40Types = ExportWarframes.Concat(ExportWeapons).Concat(ExportSentinels)
			.Where(kvp => kvp.Value.MaxLevelCap >= 40)
			.Select(kvp => kvp.Key)
			.ToFrozenSet(StringComparer.Ordinal);

		var rewardNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var relic in ExportRelics.Values) {
			foreach (var reward in relic.RelicRewards) {
				if (TryResolveReward(reward.RewardName, out var resolved)) rewardNames.Add(resolved.Name);
			}
		}
		RewardNames = [.. rewardNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

		Log.Info($"Indexes built: catalog={Catalog.Count} ducats={DucatsByName.Count} rewardNames={RewardNames.Count} level40={Level40Types.Count}");

		// The riven index reads ExportWeapons and RivenMods, both of which just changed.
		Riven.RivenData.Invalidate();
	}

	/// <summary>
	/// Resolves a relic reward's uniqueName to a display name. Rewards point either straight at
	/// a catalogue entry or at a recipe, whose result supplies the name (the recipe's own
	/// internal name does not match the card: "PolearmPrimeBlueprint" reads "Orthos Prime
	/// Blueprint" in game).
	/// </summary>
	public static bool TryResolveReward(string rewardName, out CatalogEntry resolved)
	{
		resolved = default!;
		if (string.IsNullOrEmpty(rewardName)) return false;

		var key = rewardName.Replace("/Lotus/StoreItems/", "/Lotus/", StringComparison.Ordinal);
		if (Catalog.TryGetValue(key, out var direct)) {
			resolved = direct;
			return true;
		}

		if (RecipesByUniqueName.TryGetValue(key, out var recipe) && Catalog.TryGetValue(recipe.ResultType, out var result)) {
			resolved = new CatalogEntry($"{result.Name} Blueprint", result.Icon, recipe.Ducats ?? 0);
			return true;
		}

		return false;
	}

	/// <summary>
	/// Strips the icon placeholders DE embeds in display names ("&lt;ARCHWING&gt; Odonata Prime",
	/// "&lt;RETRO_TM&gt;"). Leaving them in would break both the UI labels and the OCR match pool,
	/// since the reward card shows the name without them.
	/// </summary>
	private static string CleanDisplayName(string name)
	{
		if (string.IsNullOrEmpty(name) || !name.Contains('<')) return name;

		var builder = new System.Text.StringBuilder(name.Length);
		bool inTag = false;
		foreach (char c in name) {
			if (c == '<') {
				inTag = true;
				continue;
			}
			if (inTag) {
				if (c == '>') inTag = false;
				continue;
			}
			// Collapse the run of spaces a removed tag leaves behind.
			if (c == ' ' && (builder.Length == 0 || builder[^1] == ' ')) continue;
			builder.Append(c);
		}

		return builder.ToString().TrimEnd();
	}

	private static bool ShouldSkipWeapon(string type, ItemDTO weapon)
	{
		if (type.Contains("PvPVariant") || type.Contains("Doppelganger") || type.Contains("ANTIGEN") || type.Contains("MUTAGEN") || (type.Contains("OperatorAmplifiers") && !type.Contains("Barrel"))) return true;
		var partType = weapon.PartType;
		if (partType == null) return false;
		// moas, hounds, k-drives, zaw blades and amp prisms
		return partType != "LWPT_MOA_HEAD" && partType != "LWPT_ZANUKA_HEAD" && partType != "LWPT_HB_DECK" && partType != "LWPT_BLADE" && partType != "LWPT_AMP_OCULUS";
	}

	private static bool ShouldSkipSentinel(string type, ItemDTO sentinel)
		=> type.Contains("/Pets/", StringComparison.Ordinal) || sentinel.Category == "SpecialItems";

	/// <summary>Builds the equipment and relic lists, downloading any icons they need.</summary>
	public static async Task BuildCatalogAsync()
	{
		string blueprintPath = Path.Combine(AppContext.BaseDirectory, "assets", "blueprint.png");

		var equipment = ExportWarframes
			.Concat(ExportWeapons.Where(kvp => !ShouldSkipWeapon(kvp.Key, kvp.Value)))
			.Concat(ExportSentinels.Where(kvp => !ShouldSkipSentinel(kvp.Key, kvp.Value)))
			.ToList();

		var iconUrls = new HashSet<string>(StringComparer.Ordinal);
		foreach (var (type, item) in equipment) {
			iconUrls.Add(item.Icon);
			foreach (var url in GetIngredientIconUrls(type)) iconUrls.Add(url);
		}
		foreach (var relic in ExportRelics.Values) {
			iconUrls.Add(relic.Icon);
			foreach (var reward in relic.RelicRewards) {
				if (TryResolveReward(reward.RewardName, out var resolved)) iconUrls.Add(resolved.Icon);
			}
		}
		await DownloadIconsAsync(iconUrls);

		var items = new List<Item>(equipment.Count);
		foreach (var (type, item) in equipment) {
			// Use the catalogue's cleaned name, not the raw DTO name, or the icon placeholders
			// ("<ARCHWING> Odonata Prime") leak into the UI, the set names and the match pool.
			string name = Catalog.TryGetValue(type, out var entry) ? entry.Name : CleanDisplayName(item.Name);
			var category = ResolveCategory(type, item);
			WFMarketData.TryGetValue($"{name} Set", out var marketData);
			items.Add(new Item(name, type, BuildIngredients(name, type, blueprintPath), category, GetLocalIconPath(item.Icon), false, marketData?.Price ?? ""));
		}
		items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
		ItemsList = items;

		var byIngredient = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in items) {
			foreach (var ingredient in item.Ingredients) {
				byIngredient.TryAdd(ingredient.Name, item);
				// A relic drops a warframe component as its blueprint ("Chroma Prime Systems
				// Blueprint"), while the recipe ingredient is the crafted component ("Chroma
				// Prime Systems"). Index both spellings so the overlay resolves either.
				if (!ingredient.Name.EndsWith(" Blueprint", StringComparison.OrdinalIgnoreCase)) {
					byIngredient.TryAdd($"{ingredient.Name} Blueprint", item);
				}
			}
		}
		ItemsByIngredientName = byIngredient.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

		var relics = new List<Relic>(ExportRelics.Count);
		foreach (var (type, relic) in ExportRelics) {
			var rewards = new ObservableCollection<Reward>();
			foreach (var reward in relic.RelicRewards) {
				if (!TryResolveReward(reward.RewardName, out var resolved)) continue;
				rewards.Add(new Reward(resolved.Name, reward.RewardName, GetLocalIconPath(resolved.Icon), reward.Count, reward.Rarity, RarityColor(reward.Rarity)));
			}

			relics.Add(new Relic(relic.Name, type, GetLocalIconPath(relic.Icon), rewards) {
				Era = EraOf(relic.Name),
				Quality = QualityOf(type),
			});
		}
		relics.Sort((a, b) => {
			int era = VoidFissure.GetTierSortKey(a.Era).CompareTo(VoidFissure.GetTierSortKey(b.Era));
			if (era != 0) return era;
			int name = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
			return name != 0 ? name : GetRelicSortKey(a.Quality).CompareTo(GetRelicSortKey(b.Quality));
		});
		RelicList = relics;

		Log.Info($"Catalogue built: items={ItemsList.Count} relics={RelicList.Count} ingredientIndex={ItemsByIngredientName.Count}");
	}

	private static string ResolveCategory(string type, ItemDTO item)
	{
		if (ExportSentinels.ContainsKey(type)) return "Companions";
		if (type.Contains("/Hoverboard/", StringComparison.Ordinal)) return "Vehicles";
		if (type.Contains("/Pets/", StringComparison.Ordinal)) return "Companions";
		if (type.Contains("Amp", StringComparison.Ordinal) && type.Contains("Barrel", StringComparison.Ordinal)) return "OperatorAmps";

		return item.Category switch {
			"MechSuits" or "SpaceSuits" => "Vehicles",
			// Companion weapons have no filter of their own; group them with companions.
			"SentinelWeapons" => "Companions",
			_ => item.Category,
		};
	}

	private static string EraOf(string relicName)
	{
		if (string.IsNullOrEmpty(relicName)) return "";
		int space = relicName.IndexOf(' ');
		return space > 0 ? relicName[..space] : relicName;
	}

	private static string QualityOf(string uniqueName)
	{
		foreach (var (suffix, quality) in relicQualities) {
			if (uniqueName.EndsWith(suffix, StringComparison.Ordinal)) return quality;
		}
		return "Intact";
	}

	private static string RarityColor(string rarity) => rarity switch {
		"COMMON" => "#95543B",
		"UNCOMMON" => "#D1CFD1",
		"RARE" => "#EED78A",
		_ => "#4A4A4A",
	};

	private static IEnumerable<string> GetIngredientIconUrls(string type)
	{
		if (!ExportRecipes.TryGetValue(type, out var recipe)) yield break;

		foreach (var ingredient in recipe.Ingredients) {
			if (string.IsNullOrWhiteSpace(ingredient.Type)) continue;
			if (!Catalog.TryGetValue(ingredient.Type, out var entry)) continue;
			if (!string.IsNullOrEmpty(entry.Icon)) yield return entry.Icon;
		}
	}

	private static ObservableCollection<RecipeIngredient> BuildIngredients(string parentName, string parentType, string blueprintPath)
	{
		var result = new ObservableCollection<RecipeIngredient>();
		if (!ExportRecipes.TryGetValue(parentType, out var recipe)) return result;

		var blueprint = $"{parentName} Blueprint";
		WFMarketData.TryGetValue(blueprint, out var parentMarket);
		DucatsByName.TryGetValue(blueprint, out int blueprintDucats);
		result.Add(new RecipeIngredient(blueprint, parentType, 1, blueprintPath, parentMarket?.Price ?? "", Ducats(blueprintDucats)) {
			RecipeKey = recipe.UniqueName,
		});

		foreach (var ingredient in recipe.Ingredients) {
			if (string.IsNullOrWhiteSpace(ingredient.Type)) continue;
			if (!Catalog.TryGetValue(ingredient.Type, out var entry)) continue;

			var name = entry.Name;
			if (!WFMarketData.TryGetValue(name, out var market)) {
				WFMarketData.TryGetValue($"{name} Blueprint", out market);
			}
			DucatsByName.TryGetValue(name, out int ingredientDucats);
			// Components are priced under their blueprint name.
			if (ingredientDucats == 0) DucatsByName.TryGetValue($"{name} Blueprint", out ingredientDucats);
			if (ingredientDucats == 0) ingredientDucats = entry.Ducats;

			result.Add(new RecipeIngredient(name, ingredient.Type, ingredient.Count, GetLocalIconPath(entry.Icon), market?.Price ?? "", Ducats(ingredientDucats)));
		}
		return result;
	}

	private static string Ducats(int value) => value > 0 ? value.ToString() : "";

	/// <summary>
	/// Live platinum prices. Not part of DE's export, so this is best effort and never fatal:
	/// the rest of the app works without it.
	/// </summary>
	public static async Task LoadWFMarketData(bool updateFile)
	{
		var itemsFile = Path.Combine(AppData.CacheDir, "wfmarketitems.json");
		var pricesFile = Path.Combine(AppData.CacheDir, "wfmarketprices.json");
		try {
			if (!File.Exists(itemsFile) || updateFile) {
				await AppData.DownloadToFileAsync("https://api.warframe.market/v2/items/", itemsFile);
			}
			await AppData.DownloadToFileAsync("https://api.warframestat.us/wfinfo/prices", pricesFile);

			var priceData = new Dictionary<string, (string Volume, string Price)>(StringComparer.Ordinal);
			await using (var pricesRead = File.OpenRead(pricesFile)) {
				using var pricesDoc = await JsonDocument.ParseAsync(pricesRead);
				foreach (var item in pricesDoc.RootElement.EnumerateArray()) {
					var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
					if (string.IsNullOrEmpty(name)) continue;

					var volume = item.TryGetProperty("today_vol", out var v) ? v.ToString() : "";
					var price = item.TryGetProperty("custom_avg", out var p) ? p.ToString() : "";
					priceData[name] = (volume, price);
				}
			}

			var builder = new Dictionary<string, ItemData>(StringComparer.Ordinal);
			await using (var itemsRead = File.OpenRead(itemsFile)) {
				using var itemsDoc = await JsonDocument.ParseAsync(itemsRead);
				if (!itemsDoc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) {
					Log.Warn("warframe.market item list had an unexpected shape.");
					return;
				}

				foreach (var item in data.EnumerateArray()) {
					var slug = item.TryGetProperty("slug", out var s) ? s.GetString() ?? "" : "";
					if (string.IsNullOrEmpty(slug)) continue;

					var name = item.TryGetProperty("i18n", out var i18n) && i18n.TryGetProperty("en", out var en)
						&& en.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
					if (string.IsNullOrEmpty(name)) continue;

					var ducats = item.TryGetProperty("ducats", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32().ToString() : "";
					priceData.TryGetValue(name, out var priceInfo);
					builder[name] = new ItemData(slug, ducats, priceInfo.Price ?? "", priceInfo.Volume ?? "");
				}
			}

			WFMarketData = builder.ToFrozenDictionary(StringComparer.Ordinal);
			Log.Info($"warframe.market prices loaded: {WFMarketData.Count} entries.");
		} catch (Exception ex) {
			Log.Warn("Could not load warframe.market prices; continuing without them.", ex);
		}
	}

	public static async Task ExtractGameInfo()
	{
		Directory.CreateDirectory(AppData.AppDataDir);
		bool isWindows = OperatingSystem.IsWindows();
		var exeFileName = isWindows ? "warframe-api-helper.exe" : "warframe-api-helper";
		var exePath = Path.Combine(AppData.AppDataDir, exeFileName);
		if (!File.Exists(exePath)) {
			if (!await Notify.Confirm("Download required component", "Do you want to download warframe-api-helper from its official GitHub repository?", "Yes", "No")) {
				return;
			}

			string url = isWindows
				? "https://github.com/Sainan/warframe-api-helper/releases/download/1.1.1/warframe-api-helper.exe"
				: "https://github.com/Sainan/warframe-api-helper/releases/download/1.1.1/Linux.zip";
			var tempPath = Path.Combine(AppData.AppDataDir, Path.GetRandomFileName());
			try {
				using var inStream = await AppData.GetStreamAsync(url);
				using var outStream = File.Create(tempPath);
				await inStream.CopyToAsync(outStream);
				outStream.Close();

				if (!isWindows && url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
					try {
						System.IO.Compression.ZipFile.ExtractToDirectory(tempPath, AppData.AppDataDir);
						var extracted = Directory.EnumerateFiles(AppData.AppDataDir, "warframe-api-helper*", SearchOption.AllDirectories)
							.FirstOrDefault(f => Path.GetFileName(f).StartsWith("warframe-api-helper", StringComparison.Ordinal)) ?? throw new FileNotFoundException("Extracted helper not found.");
						if (File.Exists(exePath)) File.Delete(exePath);
						File.Move(extracted, exePath);
					} finally {
						try { File.Delete(tempPath); } catch { /* best effort */ }
					}

					try {
						using var chmod = Process.Start(new ProcessStartInfo {
							FileName = "chmod",
							Arguments = $"+x \"{exePath}\"",
							UseShellExecute = false,
							CreateNoWindow = true
						});
						if (chmod != null) await chmod.WaitForExitAsync();
					} catch (Exception ex) {
						Log.Warn("Could not mark the helper executable; it may fail to start.", ex);
					}
				} else {
					if (File.Exists(exePath)) File.Delete(exePath);
					File.Move(tempPath, exePath);
				}
			} catch (Exception ex) {
				try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
				Log.Error("Failed to download warframe-api-helper.", ex);
				Notify.Error("Error", "Failed to download component: " + ex.Message);
				return;
			}
		}

		if (!await Notify.Confirm("Disclaimer", "By confirming, you acknowledge and agree to use warframe-api-helper to retrieve your inventory data at your own risk.", "Confirm", "Close")) {
			Notify.Toast("Info", "Operation cancelled by user.");
			return;
		}

		using var process = new Process {
			StartInfo = new ProcessStartInfo {
				FileName = exePath,
				WorkingDirectory = AppData.AppDataDir,
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				RedirectStandardInput = true,
				CreateNoWindow = true,
			}
		};

		process.Start();
		try {
			await process.StandardInput.WriteLineAsync();
			await process.StandardInput.FlushAsync();
			process.StandardInput.Close();
		} catch (Exception ex) {
			Log.Warn("Could not write to the helper's standard input.", ex);
		}

		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		var stdout = await stdoutTask;
		var stderr = await stderrTask;
		if (process.ExitCode != 0) {
			var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr + Environment.NewLine + stdout;
			Log.Error($"warframe-api-helper exited with {process.ExitCode}: {msg}");
			Notify.Error("warframe-api-helper error", msg);
		}
	}

	public static long XpToMaster(string type)
	{
		int levelCap = Level40Types.Contains(type) ? 40 : 30;
		long baseXp = type.Contains("/Lotus/Powersuits/", StringComparison.Ordinal) ? 1000L : 500L;
		return baseXp * levelCap * levelCap;
	}

	public static int GetRelicSortKey(string quality) => quality switch {
		"Intact" => 1,
		"Exceptional" => 2,
		"Flawless" => 3,
		"Radiant" => 4,
		_ => int.MaxValue
	};

	public static int GetRaritySortKey(string rarity) => rarity switch {
		"COMMON" => 1,
		"UNCOMMON" => 2,
		"RARE" => 3,
		_ => int.MaxValue
	};
}
