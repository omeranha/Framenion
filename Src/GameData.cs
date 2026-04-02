using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Sdcb.PaddleOCR;
using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

public class RewardInfo(string name)
{
	public string ItemName { get; set; } = name;
	public string Platinum { get; set; } = "0";
	public string Ducats { get; set; } = "0";
}

public static class GameData
{
	public static readonly IReadOnlyDictionary<string, (string, string)> relicType = new Dictionary<string, (string, string)>(StringComparer.Ordinal) {
		["VoidT1"] = ("Lith", "#72523c"),
		["VoidT2"] = ("Meso", "#917147"),
		["VoidT3"] = ("Neo", "#c9c3c4"),
		["VoidT4"] = ("Axi", "#FFD700"),
		["VoidT5"] = ("Requiem", "#e80c1e"),
		["VoidT6"] = ("Omnia", "#FFFFFF")
	};
	public static readonly string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Framenion");
	public static readonly string cacheDir = Path.Combine(appDataDir, "cache");
	public static readonly string iconsCacheDir = Path.Combine(cacheDir, "icons");
	private static readonly SemaphoreSlim iconDownloadSemaphore = new(10, 10);
	public static readonly HttpClient httpClient = new() {
		BaseAddress = new Uri("https://browse.wf/"),
	};
	public static PaddleOcrAll? paddleEngine;

	public static JsonElement mainRoot = new();

	public static List<VoidFissure> fissures = [];
	public static List<Item> itemsList = [];

	public static FrozenDictionary<string, string> lang = FrozenDictionary<string, string>.Empty;
	public static FrozenDictionary<string, string> exportTextIcons = FrozenDictionary<string, string>.Empty;
	public static FrozenDictionary<string, string> exportMissionTypes = FrozenDictionary<string, string>.Empty;
	public static FrozenDictionary<string, string> exportFactions = FrozenDictionary<string, string>.Empty;

	public static FrozenDictionary<string, (string slug, string ducats)> warframeMarketItems = FrozenDictionary<string, (string, string)>.Empty;
	public static FrozenDictionary<string, (string name, RecipeDTO recipe)> exportRecipes = FrozenDictionary<string, (string, RecipeDTO)>.Empty;
	public static FrozenDictionary<string, ResourceDTO> exportResources = FrozenDictionary<string, ResourceDTO>.Empty;
	public static FrozenDictionary<string, ItemDTO> exportWarframes = FrozenDictionary<string, ItemDTO>.Empty;
	public static FrozenDictionary<string, ItemDTO> exportWeapons = FrozenDictionary<string, ItemDTO>.Empty;
	public static FrozenDictionary<string, ItemDTO> exportSentinels = FrozenDictionary<string, ItemDTO>.Empty;
	public static FrozenDictionary<string, RegionDTO> exportRegions = FrozenDictionary<string, RegionDTO>.Empty;

	public static List<string> uniquelevelCaps = [];
	public static List<string> primeItems = [];

	public static DispatcherTimer? fissureRefreshTimer;
	public static DispatcherTimer? fissureUpdateTimer;
	public static DispatcherTimer logPollTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

	public static AppSettings appSettings = new();

	private static readonly ConcurrentDictionary<string, Lazy<Bitmap?>> bitmapCache = new(StringComparer.Ordinal);

	public static async Task<FrozenDictionary<string, T>> Deserialize<T>(string path)
	{
		await using var stream = File.OpenRead(path);
		using var doc = await JsonDocument.ParseAsync(stream);
		var builder = new Dictionary<string, T>(StringComparer.Ordinal);
		foreach (var element in doc.RootElement.EnumerateObject()) {
			var item = JsonSerializer.Deserialize<T>(element.Value.GetRawText());
			if (item != null) {
				builder[element.Name] = item;
			}
		}
		return builder.ToFrozenDictionary(StringComparer.Ordinal);
	}

	public static Bitmap? GetOrCreateBitmap(string localPath, int decodeWidth = 80)
	{
		if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return null;
		var lazy = bitmapCache.GetOrAdd(localPath, path =>
			new Lazy<Bitmap?>(() => {
				try {
					using var fs = File.OpenRead(path);
					return Bitmap.DecodeToWidth(fs, decodeWidth, BitmapInterpolationMode.LowQuality);
				} catch {
					return null;
				}
			}, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication));
		return lazy.Value;
	}

	public static void ClearBitmapCache()
	{
		foreach (var kv in bitmapCache) {
			if (kv.Value.IsValueCreated && kv.Value.Value is IDisposable d) {
				try { d.Dispose(); } catch { }
			}
		}
		bitmapCache.Clear();
	}

	public static string GetLocalIconPath(string icon)
	{
		if (string.IsNullOrEmpty(icon)) return "";
		if (icon.Contains("/CraftingComponents/")) {
			return Path.Combine(iconsCacheDir, icon.Split("/CraftingComponents/")[1]);
		}
		return Path.Combine(iconsCacheDir, icon.Split('/').Last());
	}

	private static async Task DownloadIconAsync(string icon)
	{
		var iconPath = GetLocalIconPath(icon);
		if (File.Exists(iconPath)) return;

		await iconDownloadSemaphore.WaitAsync();
		try {
			if (File.Exists(iconPath)) return;
			using var iconResp = await httpClient.GetAsync(icon, HttpCompletionOption.ResponseHeadersRead);
			iconResp.EnsureSuccessStatusCode();
			await using var iconStream = await iconResp.Content.ReadAsStreamAsync();
			await using var fileStream = File.Create(iconPath);
			await iconStream.CopyToAsync(fileStream);
		} finally {
			iconDownloadSemaphore.Release();
		}
	}

	public static async Task DownloadIconsAsync(IEnumerable<string> icons)
	{
		var failures = new ConcurrentBag<string>();
		await Task.WhenAll(icons.Where(i => !string.IsNullOrEmpty(i)).Select(async icon => {
			try {
				await DownloadIconAsync(icon);
			} catch {
			}
		}));
	}

	public static async Task LoadWFMarketData(Window window, bool updateFile)
	{
		var cacheFile = Path.Combine(cacheDir, "wfmarketitems.json");
		try {
			if (updateFile) {
				var url = "https://api.warframe.market/v2/items/";
				using var resp = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
				resp.EnsureSuccessStatusCode();
				await using var stream = await resp.Content.ReadAsStreamAsync();

				using var fileStream = File.Create(cacheFile);
				await stream.CopyToAsync(fileStream);
			}

			using var cacheStream = File.OpenRead(cacheFile);
			using var doc = await JsonDocument.ParseAsync(cacheStream);
			var builder = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
			if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array) {
				foreach (var item in data.EnumerateArray()) {
					item.TryGetProperty("slug", out var slugProp);
					item.TryGetProperty("i18n", out var i18nProp);
					i18nProp.TryGetProperty("en", out var enProp);
					enProp.TryGetProperty("name", out var nameProp);
					var slug = slugProp.GetString() ?? "";
					var ducats = "0";
					if (item.TryGetProperty("ducats", out var ducatsProp) && ducatsProp.ValueKind == JsonValueKind.Number) {
						ducats = ducatsProp.GetInt32().ToString();
					}
					var name = nameProp.GetString() ?? "";
					builder[name] = (slug, ducats);
				}
			}

			warframeMarketItems = builder.ToFrozenDictionary(StringComparer.Ordinal);
		} catch (Exception ex) {
			MessageBox.Show(window, "Error", "Failed to load Warframe Market items: " + ex.Message);
		}
	}

	public static async Task LoadFile(Window window, string file, string cacheDir, bool updateFile)
	{
		var exportCacheFile = Path.Combine(cacheDir, file + ".json");
		if (!File.Exists(exportCacheFile) || updateFile) {
			try {
				var url = "https://raw.githubusercontent.com/calamity-inc/warframe-public-export-plus/refs/heads/senpai/" + file + ".json";

				using var resp = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
				resp.EnsureSuccessStatusCode();

				await using var inStream = await resp.Content.ReadAsStreamAsync();
				await using var outStream = File.Create(exportCacheFile);
				await inStream.CopyToAsync(outStream);
			} catch {
				throw new FileNotFoundException("Failed to retrieve file: " + file);
			}
		}

		try {
			switch (file) {
				case "dict.en": {
						using var stream = File.OpenRead(exportCacheFile);
						using var doc = await JsonDocument.ParseAsync(stream);
						var builder = new Dictionary<string, string>(StringComparer.Ordinal);
						foreach (var prop in doc.RootElement.EnumerateObject()) {
							if (prop.Name.Contains("/CraftingComponent_")) {
								primeItems.Add(prop.Value.GetString() ?? "");
							}
							builder[prop.Name] = prop.Value.ToString();
						}
						lang = builder.ToFrozenDictionary();
						break;
					}
				case "ExportWarframes":
					exportWarframes = await Deserialize<ItemDTO>(exportCacheFile);
					break;
				case "ExportWeapons":
					exportWeapons = await Deserialize<ItemDTO>(exportCacheFile);
					break;
				case "ExportSentinels":
					exportSentinels = await Deserialize<ItemDTO>(exportCacheFile);
					break;
				case "ExportRecipes": {
						await using var stream = File.OpenRead(exportCacheFile);
						using var doc = await JsonDocument.ParseAsync(stream);
						var builder = new Dictionary<string, (string, RecipeDTO)>(StringComparer.Ordinal);

						foreach (var r in doc.RootElement.EnumerateObject()) {
							if (r.Value.TryGetProperty("resultType", out var rt) && rt.GetString() is string rtStr) {
								var recipe = JsonSerializer.Deserialize<RecipeDTO>(r.Value);
								if (recipe == null) continue;
								builder[rtStr] = (r.Name, recipe);
							}
						}
						exportRecipes = builder.ToFrozenDictionary();
						break;
					}
				case "ExportRegions":
					exportRegions = await Deserialize<RegionDTO>(exportCacheFile);
					break;
				case "ExportResources":
					exportResources = await Deserialize<ResourceDTO>(exportCacheFile);
					break;
				case "ExportMissionTypes": {
						using var stream = File.OpenRead(exportCacheFile);
						using var doc = await JsonDocument.ParseAsync(stream);
						var builder = new Dictionary<string, string>(StringComparer.Ordinal);
						foreach (var prop in doc.RootElement.EnumerateObject()) {
							if (prop.Value.TryGetProperty("name", out var nameProp)) {
								builder[prop.Name] = nameProp.GetString() ?? "";
							}
						}
						exportMissionTypes = builder.ToFrozenDictionary();
						break;
					}
				case "ExportFactions": {
						using var stream = File.OpenRead(exportCacheFile);
						using var doc = await JsonDocument.ParseAsync(stream);
						var builder = new Dictionary<string, string>(StringComparer.Ordinal);
						foreach (var prop in doc.RootElement.EnumerateObject()) {
							if (prop.Value.TryGetProperty("name", out var nameProp)) {
								builder[prop.Name] = nameProp.GetString() ?? "";
							}
						}
						exportFactions = builder.ToFrozenDictionary();
						break;
					}
				case "ExportTextIcons": {
						using var stream = File.OpenRead(exportCacheFile);
						using var doc = await JsonDocument.ParseAsync(stream);
						var builder = new Dictionary<string, string>(StringComparer.Ordinal);
						foreach (var prop in doc.RootElement.EnumerateObject()) {
							if (prop.Value.TryGetProperty("DIT_AUTO", out var nameProp)) {
								builder[prop.Name] = nameProp.GetString() ?? "";
							}
						}
						exportTextIcons = builder.ToFrozenDictionary();
						break;
					}
				case "ExportMisc": {
						await using var stream = File.OpenRead(exportCacheFile);
						using var doc = await JsonDocument.ParseAsync(stream);
						foreach (var prop in doc.RootElement.GetProperty("uniqueLevelCaps").EnumerateObject()) {
							uniquelevelCaps.Add(prop.Name);
						}
						break;
					}
			}
		} catch (Exception ex) {
			MessageBox.Show(window, "Error", $"Error loading {file}: {ex.Message}");
		}
	}

	private static string ResolveName(string langKey)
	{
		var name = lang.TryGetValue(langKey, out var value) ? value : langKey;
		return name.Replace("<ARCHWING> ", "");
	}

	private static IEnumerable<string> GetIngredientIconUrls(string type)
	{
		if (!exportRecipes.TryGetValue(type, out var recipe)) yield break;
		var ingredients = recipe.recipe.Ingredients;
		if (ingredients == null || ingredients.Count < 1 ) yield break;
		foreach (var ingredient in ingredients) {
			var ingredientType = ingredient.Type;
			if (string.IsNullOrWhiteSpace(ingredientType)) continue;
			if (!exportResources.TryGetValue(ingredientType, out var resource) || resource == null) continue;
			var icon = resource.Icon;
			if (!string.IsNullOrEmpty(icon)) yield return icon;
		}
	}

	private static ObservableCollection<RecipeIngredient> BuildIngredients(string parentName, string parentType, string blueprintPath)
	{
		var result = new ObservableCollection<RecipeIngredient>();
		if (!exportRecipes.TryGetValue(parentType, out var recipe)) return result;
		var ingredients = recipe.recipe.Ingredients;
		if (ingredients == null) return result;

		result.Add(new RecipeIngredient(parentName + " Blueprint", parentType, 1, blueprintPath) { RecipeKey = recipe.name});
		foreach (var ingredient in ingredients) {
			var ingredientType = ingredient.Type;
			if (string.IsNullOrWhiteSpace(ingredientType)) continue;
			if (!exportResources.TryGetValue(ingredientType, out var resource) || resource == null) continue;
			var ingredientLangKey = resource.Name;
			result.Add(new RecipeIngredient(
				ResolveName(ingredientLangKey),
				ingredientType,
				ingredient.Count,
				GetLocalIconPath(resource.Icon)
			));
		}
		return result;
	}

	private static bool ShouldSkipWeapon(string type, ItemDTO weapon)
	{
		if (type.Contains("PvPVariant") || type.Contains("Doppelganger")) return true;
		var partType = weapon.PartType;
		if (partType == null) return false;
		// moas, hounds, k-drives, zaw blades and amp prisms
		return partType != "LWPT_MOA_HEAD" && partType != "LWPT_ZANUKA_HEAD" && partType != "LWPT_HB_DECK" && partType != "LWPT_BLADE" && partType != "LWPT_AMP_OCULUS";
	}

	public static async Task LoadExports(Window window)
	{
		string blueprintPath = Path.Combine(AppContext.BaseDirectory, "assets", "blueprint.png");
		try {
			itemsList.Clear();
			var iconUrls = new HashSet<string>(StringComparer.Ordinal);
			foreach (var (type, element) in exportWarframes
				.Concat(exportWeapons.Where(kvp => !ShouldSkipWeapon(kvp.Key, kvp.Value)))
				.Concat(exportSentinels.Where(kvp => !kvp.Key.Contains("/Pets/")))) {
				iconUrls.Add(element.Icon);
				foreach (var url in GetIngredientIconUrls(type)) iconUrls.Add(url);
			}
			await DownloadIconsAsync(iconUrls);

			foreach (var (type, warframe) in exportWarframes) {
				var name = ResolveName(warframe.Name);
				var category = warframe.Category;
				if (category is "MechSuits" or "SpaceSuits") category = "Vehicles";
				itemsList.Add(new Item(name, type, BuildIngredients(name, type, blueprintPath), category, GetLocalIconPath(warframe.Icon), false));
			}

			foreach (var (type, weapon) in exportWeapons) {
				if (ShouldSkipWeapon(type, weapon)) continue;
				var name = ResolveName(weapon.Name);
				var category = weapon.Category;
				if (type.Contains("/Hoverboard/")) category = "Vehicles";
				else if (type.Contains("/Pets/")) category = "Companions";
				else if (type.Contains("Amp") && type.Contains("Barrel")) category = "OperatorAmps";
				itemsList.Add(new Item(name, type, BuildIngredients(name, type, blueprintPath), category, GetLocalIconPath(weapon.Icon), false));
			}

			foreach (var (type, sentinel) in exportSentinels) {
				if (type.Contains("/Pets/")) continue;
				var name = ResolveName(sentinel.Name);
				itemsList.Add(new Item(name, type, BuildIngredients(name, type, blueprintPath), "Companions", GetLocalIconPath(sentinel.Icon), false));
			}
		} catch (Exception ex) {
			MessageBox.Show(window, "Error", "Failed to load exports: " + ex.Message);
		}
	}

	public static int GetTierSortKey(string voidTier)
	{
		if (voidTier.StartsWith("VoidT", StringComparison.Ordinal) && int.TryParse(voidTier.AsSpan("VoidT".Length), out var n)) {
			return n;
		}
		return 0;
	}

	public static async Task ExtractGameInfo(Window window)
	{
		Directory.CreateDirectory(GameData.appDataDir);
		bool isWindows = OperatingSystem.IsWindows();
		var exeFileName = isWindows ? "warframe-api-helper.exe" : "warframe-api-helper";
		var exePath = Path.Combine(GameData.appDataDir, exeFileName);
		if (!File.Exists(exePath)) {
			if (!await MessageBox.AskYesNo(window, "Download required component", "Do you want to download warframe-api-helper from its official GitHub repository?")) {
				return;
			}

			string url = isWindows
				? "https://github.com/Sainan/warframe-api-helper/releases/download/1.1.1/warframe-api-helper.exe"
				: "https://github.com/Sainan/warframe-api-helper/releases/download/1.1.1/Linux.zip";
			var tempPath = Path.Combine(GameData.appDataDir, Path.GetRandomFileName());
			try {
				using var download = await GameData.httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
				download.EnsureSuccessStatusCode();
				await using (var inStream = await download.Content.ReadAsStreamAsync())
				await using (var outStream = File.Create(tempPath)) {
					await inStream.CopyToAsync(outStream);
				}

				if (!isWindows && url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) {
					try {
						System.IO.Compression.ZipFile.ExtractToDirectory(tempPath, GameData.appDataDir);
						var extracted = Directory.EnumerateFiles(GameData.appDataDir, "warframe-api-helper*", SearchOption.AllDirectories)
							.FirstOrDefault(f => Path.GetFileName(f).StartsWith("warframe-api-helper", StringComparison.Ordinal)) ?? throw new FileNotFoundException("Extracted helper not found.");
						if (File.Exists(exePath)) File.Delete(exePath);
						File.Move(extracted, exePath);
					} finally {
						try { File.Delete(tempPath); } catch { }
					}

					if (!isWindows) {
						try {
							using var chmod = new Process {
								StartInfo = new ProcessStartInfo {
									FileName = "chmod",
									Arguments = $"+x \"{exePath}\"",
									UseShellExecute = false,
									CreateNoWindow = true
								}
							};
							chmod.Start();
							await chmod.WaitForExitAsync();
						} catch {
							// todo: test proceed but the helper might fail without exec bit
						}
					}
				} else {
					if (File.Exists(exePath)) File.Delete(exePath);
					File.Move(tempPath, exePath);
				}
			} catch (Exception ex) {
				try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
				MessageBox.Show(window, "Error", "Failed to download component: " + ex.Message);
				return;
			}
		}

		if (!await MessageBox.AskYesNo(window, "Disclaimer", "By confirming, you acknowledge and agree to use warframe-api-helper to retrieve your inventory data at your own risk.", "Confirm", "Close")) {
			MessageBox.Show(window, "Info", "Operation cancelled by user.");
			return;
		}

		using var process = new Process {
			StartInfo = new ProcessStartInfo {
				FileName = exePath,
				WorkingDirectory = GameData.appDataDir,
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
		} catch { }

		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync();
		var stdout = await stdoutTask;
		var stderr = await stderrTask;
		if (process.ExitCode != 0) {
			var msg = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr + Environment.NewLine + stdout;
			MessageBox.Show(window, "warframe-api-helper error", msg);
			return;
		}
	}
}
