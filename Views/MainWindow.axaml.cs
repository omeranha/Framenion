using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using framenion.Src;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion;

public partial class MainWindow : Window
{
	private readonly string[] files = ["dict.en", "ExportWarframes", "ExportRecipes", "ExportWeapons", "ExportRegions", "ExportResources", "ExportMisc", "ExportSentinels", "ExportTextIcons", "ExportMissionTypes", "ExportFactions"];
	public ObservableCollection<Item> displayedItems = [];
	public ObservableCollection<VoidFissure> displayedFissures = [];

	private string searchText = string.Empty;
	private string currentItemsFilter = "All";
	private string currentFissureFilter = "Normal";

	private static readonly string EElog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Warframe/EE.log");
	private static readonly string inventoryFile = Path.Combine(GameData.appDataDir, "inventory.json");

	private readonly HashSet<string> _seenFissureIds = new(StringComparer.Ordinal);

	public MainWindow()
	{
		Directory.CreateDirectory(GameData.cacheDir);
		Directory.CreateDirectory(GameData.iconsCacheDir);
		Initialized += async (s, e) => {
			await InitializeAsync();
		};

		InitializeComponent(true);
		ItemsList.ItemsSource = displayedItems;
		FissuresList.ItemsSource = displayedFissures;
	}

	private async Task InitializeAsync()
	{
		try {
			var loadTasks = files.Select(f => GameData.LoadFile(this, f, GameData.cacheDir));
			await Task.WhenAll(loadTasks);

			GameData.fissureRefreshTimer = new DispatcherTimer {
				Interval = TimeSpan.FromMinutes(1)
			};
			GameData.fissureRefreshTimer.Tick += async (s, e) => {
				await VoidFissure.LoadVoidFissures(this);
				RefreshFissuresList();
			};

			GameData.fissureUpdateTimer = new DispatcherTimer {
				Interval = TimeSpan.FromSeconds(1)
			};
			GameData.fissureUpdateTimer.Tick += (s, e) => UpdateFissureTimers();
			GameData.fissureUpdateTimer?.Start();
			GameData.fissureRefreshTimer?.Start();

			await VoidFissure.LoadVoidFissures(this);
			foreach (var f in GameData.fissures) _seenFissureIds.Add(f.Id);
			Dispatcher.UIThread.Invoke(() => RefreshFissuresList());

			await GameData.LoadExports(this);
			await ParseInfo();
			Dispatcher.UIThread.Invoke(() => RefreshItemsList());
		} catch (Exception ex) {
			MessageBox.Show(this, "Error", "Failed to initialize application: " + ex.Message);
		}
	}

	private async Task ParseInfo()
	{
		if (!File.Exists(inventoryFile)) {
			return;
		}

		await using var stream = File.OpenRead(inventoryFile);
		using var doc = await JsonDocument.ParseAsync(stream);
		var root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object) return;
		GameData.mainRoot = root;

		if (File.Exists(EElog)) {
			var accountId = File.ReadLines(EElog).FirstOrDefault(line => line.Contains("AccountId: "))?.Split("AccountId: ")[1].Trim();
			var user_resp = await GameData.httpClient.GetAsync($"http://content.warframe.com/dynamic/getProfileViewingData.php?playerId={accountId}");
			user_resp.EnsureSuccessStatusCode();
			var json = await JsonDocument.ParseAsync(await user_resp.Content.ReadAsStreamAsync());
			PlayerName.Text = json.RootElement.GetProperty("Results")[0].GetProperty("DisplayName").ToString()?[..^1];
		}
		root.TryGetProperty("PremiumCredits", out var platinum);
		PlatinumText.Text = long.Parse(platinum.ToString()).ToString("N0");
		root.TryGetProperty("RegularCredits", out var credits);
		CreditsText.Text = long.Parse(credits.ToString()).ToString("N0");
		root.TryGetProperty("PlayerLevel", out var mr);
		var mr_str = (mr.GetUInt16() > 30) ? "L" + (mr.GetUInt16() - 30) : mr.ToString();
		MasteryText.Text = mr_str;
		GameData.exportTextIcons.TryGetValue($"RANK_{mr}", out var rankIconEl);
		if (rankIconEl.TryGetProperty("DIT_AUTO", out var rankIconPathEl)) {
			var icon_resp = await GameData.httpClient.GetAsync(rankIconPathEl.ToString());
			var bitmap = new Bitmap(await icon_resp.Content.ReadAsStreamAsync());
			MasteryIcon.Source = bitmap;
		}

		root.TryGetProperty("ActiveAvatarImageType", out var icon_path);
		var glyph_resp = await GameData.httpClient.GetAsync(icon_path.ToString());
		glyph_resp.EnsureSuccessStatusCode();
		var glyph_doc = await JsonDocument.ParseAsync(await glyph_resp.Content.ReadAsStreamAsync());
		if (glyph_doc.RootElement.ValueKind == JsonValueKind.Object) {
			glyph_doc.RootElement.TryGetProperty("icon", out var icon_url);
			var icon_resp = await GameData.httpClient.GetAsync(icon_url.ToString());
			var bitmap = new Bitmap(await icon_resp.Content.ReadAsStreamAsync());
			PlayerIcon.Source = bitmap;
		}

		if (!GameData.mainRoot.TryGetProperty("MiscItems", out var miscEl) ||
			!GameData.mainRoot.TryGetProperty("Recipes", out var recipesEl) ||
			!GameData.mainRoot.TryGetProperty("XPInfo", out var xpInfoEl) ||
			!GameData.mainRoot.TryGetProperty("MechSuits", out var mechEl) ||
			!GameData.mainRoot.TryGetProperty("Suits", out var warframeEl) ||
			!GameData.mainRoot.TryGetProperty("SpaceSuits", out var archwingsEl) ||
			!GameData.mainRoot.TryGetProperty("LongGuns", out var primaryEl) ||
			!GameData.mainRoot.TryGetProperty("Melee", out var meleeEl) ||
			!GameData.mainRoot.TryGetProperty("Pistols", out var pistolsEl) ||
			!GameData.mainRoot.TryGetProperty("SpaceGuns", out var archweaponsEl) ||
			!GameData.mainRoot.TryGetProperty("SpaceMelee", out var archmeleeEl) ||
			!GameData.mainRoot.TryGetProperty("Sentinels", out var sentinelsEl) ||
			!GameData.mainRoot.TryGetProperty("SentinelWeapons", out var sentinelWeaponsEl) ||
			!GameData.mainRoot.TryGetProperty("KubrowPets", out var petsEl)) return;

		var xpByType = new Dictionary<string, long>(StringComparer.Ordinal);
		foreach (var e in xpInfoEl.EnumerateArray())
			if (e.TryGetProperty("ItemType", out var t) && e.TryGetProperty("XP", out var xp)
				&& t.GetString() is string tStr && xp.ValueKind == JsonValueKind.Number)
				xpByType[tStr] = xp.GetInt64();

		var ownedTypes = new HashSet<string>(StringComparer.Ordinal);
		foreach (var arr in new[] { warframeEl, mechEl, meleeEl, primaryEl, pistolsEl, archwingsEl, archweaponsEl, archmeleeEl, sentinelsEl, sentinelWeaponsEl, petsEl })
			foreach (var e in arr.EnumerateArray())
				if (e.TryGetProperty("ItemType", out var t) && t.GetString() is string tStr)
					ownedTypes.Add(tStr);

		var miscByType = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		foreach (var e in miscEl.EnumerateArray())
			if (e.TryGetProperty("ItemType", out var t) && t.GetString() is string tStr)
				miscByType[tStr] = e;

		var recipesByType = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		foreach (var e in recipesEl.EnumerateArray())
			if (e.TryGetProperty("ItemType", out var t) && t.GetString() is string tStr)
				recipesByType[tStr] = e;

		foreach (var item in GameData.itemsList) {
			var resultType = item.Type;
			xpByType.TryGetValue(resultType, out var xpForItem);
			if (xpForItem >= XpToMaster(resultType)) item.Mastered = true;
			if (ownedTypes.Contains(resultType)) item.BorderColor = "#3aba29";
			foreach (var ingred in item.Ingredients) {
				var type = ingred.ItemType;
				if (!miscByType.TryGetValue(type, out var ingred_entry) && !recipesByType.TryGetValue(ingred.RecipeKey, out ingred_entry)) {
					continue;
				}

				var count = ingred_entry.GetProperty("ItemCount");
				ingred.OwnedCount = count.GetInt32();
				if (GameData.exportRecipes.TryGetValue(type, out var subRecipe)
					&& subRecipe.Item2.TryGetProperty("ingredients", out var subIngredientsEl)
					&& subIngredientsEl.ValueKind == JsonValueKind.Array) {
					bool canCraft = subIngredientsEl.EnumerateArray().All(sub =>
						sub.TryGetProperty("ItemType", out var st) && st.GetString() is string subType
						&& sub.TryGetProperty("ItemCount", out var sc)
						&& miscByType.TryGetValue(subType, out var subEntry)
						&& subEntry.TryGetProperty("ItemCount", out var ownedEl)
						&& ownedEl.GetInt32() >= sc.GetInt32());
					if (canCraft && ingred.BackgroundColor == "#252525") ingred.BorderColor = "#4A9EFF";
				}
			}

			if (item.Ingredients.Count > 0 && item.Ingredients.All(i => i.OwnedCount >= i.Count)) item.BorderColor = "#4A9EFF";
		}
		RefreshItemsList();
	}

	private static long XpToMaster(string type)
	{
		int levelCap = 30;
		if (GameData.exportMisc == null || GameData.exportMisc.RootElement.ValueKind != JsonValueKind.Object)
			return levelCap;

		if (GameData.exportMisc.RootElement.TryGetProperty("uniqueLevelCaps", out var levelCapsEl) && levelCapsEl.ValueKind == JsonValueKind.Object) {
			if (levelCapsEl.TryGetProperty(type, out var capEl) && capEl.ValueKind == JsonValueKind.Number) {
				levelCap = capEl.GetInt32();
			}
		}

		bool isWarframe = type.Contains("/Lotus/Powersuits/", StringComparison.Ordinal);
		long baseXp = isWarframe ? 1000L : 500L;
		return baseXp * (long)levelCap * (long)levelCap;
	}

	private async Task ExtractGameInfo()
	{
		Directory.CreateDirectory(GameData.appDataDir);
		bool isWindows = OperatingSystem.IsWindows();
		var exeFileName = isWindows ? "warframe-api-helper.exe" : "warframe-api-helper";
		var exePath = Path.Combine(GameData.appDataDir, exeFileName);
		if (!File.Exists(exePath)) {
			if (await MessageBox.AskYesNo(this, "Download required component", "Do you want to download warframe-api-helper from its official GitHub repository?")) {
				string url = isWindows
					? "https://github.com/Sainan/warframe-api-helper/releases/download/1.1.1/warframe-api-helper.exe"
					: "https://github.com/Sainan/warframe-api-helper/releases/download/1.1.1/Linux.zip";
				var tempPath = Path.Combine(GameData.appDataDir, Path.GetRandomFileName());
				try {
					var download = await GameData.httpClient.GetAsync(url);
					download.EnsureSuccessStatusCode();
					await using (var outStream = File.Create(tempPath)) {
						await (await download.Content.ReadAsStreamAsync()).CopyToAsync(outStream);
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
								// proceed but the helper might fail without exec bit
							}
						}
					} else {
						if (File.Exists(exePath)) File.Delete(exePath);
						File.Move(tempPath, exePath);
					}
				} catch (Exception ex) {
					try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
					MessageBox.Show(this, "Error", "Failed to download component: " + ex.Message);
					return;
				}
			}
		}

		if (!await MessageBox.AskYesNo(this, "Disclaimer", "By confirming, you acknowledge and agree to use warframe-api-helper to retrieve your inventory data at your own risk.", "Confirm", "Close")) {
			MessageBox.Show(this, "Info", "Operation cancelled by user.");
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
			MessageBox.Show(this, "warframe-api-helper error", msg);
			return;
		}
		await ParseInfo();
	}

	private void UpdateFissureTimers()
	{
		foreach (var fissure in displayedFissures) {
			fissure.UpdateTimeRemaining();
		}

		var expired = displayedFissures.Where(f => f.Expiry <= DateTime.UtcNow).ToList();
		foreach (var expiredFissure in expired) {
			displayedFissures.Remove(expiredFissure);
			GameData.fissures.Remove(expiredFissure);
		}
	}

	private void RefreshItemsList()
	{
		displayedItems.Clear();
		var filtered = GameData.itemsList.AsEnumerable();
		filtered = currentItemsFilter switch {
			"Warframes" => filtered.Where(r => r.Category == "Suits"),
			"Primary" => filtered.Where(r => r.Category == "LongGuns"),
			"Secondary" => filtered.Where(r => r.Category == "Pistols"),
			"Melee" => filtered.Where(r => r.Category == "Melee"),
			"Archguns" => filtered.Where(r => r.Category == "SpaceGuns"),
			"Archmelee" => filtered.Where(r => r.Category == "SpaceMelee"),
			"Companions" => filtered.Where(r => r.Category == "Companions"),
			"Vehicles" => filtered.Where(r => r.Category == "Vehicles"),
			"Amps" => filtered.Where(r => r.Category == "OperatorAmps"),
			_ => filtered,
		};

		if (!string.IsNullOrWhiteSpace(searchText)) {
			filtered = filtered.Where(r => r.Name.Contains(searchText, StringComparison.InvariantCultureIgnoreCase));
		}

		filtered = filtered.OrderBy(r => r.Name);
		foreach (var recipe in filtered) {
			displayedItems.Add(recipe);
		}
	}

	private async void RefreshFissuresList()
	{
		displayedFissures.Clear();

		var filters = FissureAlertFilter.Load();

		var filtered = GameData.fissures.AsEnumerable();
		filtered = currentFissureFilter switch {
			"Normal" => filtered.Where(f => !f.IsHard),
			"SteelPath" => filtered.Where(f => f.IsHard),
			_ => filtered
		};

		var tierOrder = new[] { "Lith", "Meso", "Neo", "Axi", "Requiem", "Omnia" };
		var grouped = filtered.OrderBy(f => Array.IndexOf(tierOrder, f.Tier)).ThenBy(f => f.MissionType);

		foreach (var fissure in grouped) {
			fissure.IsAlertMatch = FissureAlertFilter.MatchesAny(fissure, filters);
			displayedFissures.Add(fissure);
			var isNew = _seenFissureIds.Add(fissure.Id);
			if (!isNew) continue;

			if (!fissure.IsAlertMatch) continue;

			string mode = fissure.IsHard ? "Steel Path" : "Normal";
			_ = ToastWindow.ShowToastAsync(this, $"{mode} Fissure Alert", $"A {fissure.Tier} {fissure.MissionType} on {fissure.Planet} has been opened", TimeSpan.FromSeconds(5));
		}
	}

	private void OnItemsClick(object? sender, RoutedEventArgs e)
	{
		ItemsButton.Classes.Set("selected", true);
		InventoryButton.Classes.Set("selected", false);
		ItemsContent.IsVisible = true;
		RefreshItemsList();
	}

	private void OnInventoryClick(object? sender, RoutedEventArgs e)
	{
		ItemsButton.Classes.Set("selected", false);
		InventoryButton.Classes.Set("selected", true);
		ItemsContent.IsVisible = false;
	}

	private void OnFilterClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button button) return;

		FilterAllButton.Classes.Set("selected", false);
		FilterWarframesButton.Classes.Set("selected", false);
		FilterPrimaryButton.Classes.Set("selected", false);
		FilterSecondaryButton.Classes.Set("selected", false);
		FilterMeleeButton.Classes.Set("selected", false);
		FilterArchgunsButton.Classes.Set("selected", false);
		FilterArchmeleeButton.Classes.Set("selected", false);
		FilterVehiclesButton.Classes.Set("selected", false);
		FilterCompanionsButton.Classes.Set("selected", false);
		FilterAmpsButton.Classes.Set("selected", false);
		button.Classes.Set("selected", true);
		currentItemsFilter = button.Tag?.ToString() ?? "All";
		RefreshItemsList();
	}

	private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
	{
		searchText = ItemsSearchBox.Text?.ToLowerInvariant() ?? string.Empty;
		RefreshItemsList();
	}

	private void OnFissureFilterClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button button) return;

		FissureFilterNormalButton.Classes.Set("selected", false);
		FissureFilterSteelPathButton.Classes.Set("selected", false);
		button.Classes.Set("selected", true);
		currentFissureFilter = button.Tag?.ToString() ?? "Normal";
		RefreshFissuresList();
	}

	private async void OnRefreshFissuresClick(object? sender, RoutedEventArgs e)
	{
		await VoidFissure.LoadVoidFissures(this);
		RefreshFissuresList();
	}

	private async void OnRefreshInfoClickAsync(object? sender, RoutedEventArgs e)
	{
		await ExtractGameInfo();
	}

	private async void OnFilterListClick(object? sender, RoutedEventArgs e)
	{
		try {
			var filterWindow = new FissuresFilter();
			await filterWindow.ShowDialog(this);
		} catch (Exception ex) {
			MessageBox.Show(this, "Error", "Failed to open Fissures Filter: " + ex.Message);
		}
	}
}