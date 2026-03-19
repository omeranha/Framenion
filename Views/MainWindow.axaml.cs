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
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
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
	private static readonly string notifiedFissuresFile = Path.Combine(GameData.appDataDir, "notified_fissures.txt");

	private readonly HashSet<string> notifiedFissures = new(StringComparer.Ordinal);

	private IReadOnlyList<FissureAlertEntry> loadedFissureAlertList = [];

	private CancellationTokenSource? searchDebounce;

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
				await RefreshFissuresList();
			};

			GameData.fissureUpdateTimer = new DispatcherTimer {
				Interval = TimeSpan.FromSeconds(1)
			};
			GameData.fissureUpdateTimer.Tick += (s, e) => UpdateFissureTimers();
			GameData.fissureUpdateTimer?.Start();
			GameData.fissureRefreshTimer?.Start();

			await LoadNotifiedFissures();
			loadedFissureAlertList = FissureAlertList.Load();

			await VoidFissure.LoadVoidFissures(this);
			await RefreshFissuresList();

			await GameData.LoadExports(this);
			await ParseInfo();

			RefreshItemsList();
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

		var accountId = TryReadAccountIdFromEeLog(EElog);
		if (!string.IsNullOrWhiteSpace(accountId)) {
			using var userResp = await GameData.httpClient.GetAsync($"http://content.warframe.com/dynamic/getProfileViewingData.php?playerId={accountId}", HttpCompletionOption.ResponseHeadersRead);

			userResp.EnsureSuccessStatusCode();
			await using var userStream = await userResp.Content.ReadAsStreamAsync();
			using var json = await JsonDocument.ParseAsync(userStream);
			PlayerName.Text = json.RootElement.GetProperty("Results")[0].GetProperty("DisplayName").ToString()?[..^1];
		}

		if (root.TryGetProperty("PremiumCredits", out var platinum) && platinum.TryGetInt64(out var p))
			PlatinumText.Text = p.ToString("N0");
		if (root.TryGetProperty("RegularCredits", out var credits) && credits.TryGetInt64(out var c))
			CreditsText.Text = c.ToString("N0");

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

		// (7) cache xp requirement calculation
		var xpToMasterCache = new Dictionary<string, long>(StringComparer.Ordinal);

		foreach (var item in GameData.itemsList) {
			var resultType = item.Type;

			xpByType.TryGetValue(resultType, out var xpForItem);
			if (!xpToMasterCache.TryGetValue(resultType, out var requiredXp)) {
				requiredXp = XpToMaster(resultType);
				xpToMasterCache[resultType] = requiredXp;
			}

			if (xpForItem >= requiredXp) item.Mastered = true;
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

	private static string? TryReadAccountIdFromEeLog(string eeLogPath)
	{
		if (!File.Exists(eeLogPath)) return null;
		try {
			using var fs = new FileStream(eeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
			using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
			string? line;
			while ((line = reader.ReadLine()) is not null) {
				var idx = line.IndexOf("AccountId: ", StringComparison.Ordinal);
				if (idx < 0) continue;
				return line[(idx + "AccountId: ".Length)..].Trim();
			}
		} catch (IOException) {
		}
		return null;
	}

	private void UpdateFissureTimers()
	{
		var now = DateTime.UtcNow;
		var expiredIds = new List<string>();
		for (int i = displayedFissures.Count - 1; i >= 0; i--) {
			var fissure = displayedFissures[i];
			fissure.UpdateTimeRemaining();

			if (fissure.Expiry <= now) {
				expiredIds.Add(fissure.Id);
				displayedFissures.RemoveAt(i);
				GameData.fissures.Remove(fissure);
			}
		}

		if (expiredIds.Count == 0) return;

		bool removedAnyNotified = false;
		foreach (var id in expiredIds) {
			removedAnyNotified |= notifiedFissures.Remove(id);
		}

		if (removedAnyNotified) {
			_ = SaveNotifiedFissures();
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
			filtered = filtered.Where(r => r.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
		}

		filtered = filtered.OrderBy(r => r.Name);
		foreach (var recipe in filtered) {
			displayedItems.Add(recipe);
		}
	}

	private async Task RefreshFissuresList()
	{
		displayedFissures.Clear();
		var ordered = GameData.fissures
			.OrderBy(f => GameData.GetTierSortKey(f.Tier))
			.ThenBy(f => f.MissionType)
			.ToList();

		var matches = new List<VoidFissure>();
		foreach (var fissure in ordered) {
			fissure.ShouldNotify = FissureAlertList.MatchesAny(fissure, loadedFissureAlertList);

			if (fissure.ShouldNotify && !notifiedFissures.Contains(fissure.Id)) {
				notifiedFissures.Add(fissure.Id);
				matches.Add(fissure);
			}

			if (currentFissureFilter == "Normal" && fissure.IsHard) continue;
			if (currentFissureFilter == "SteelPath" && !fissure.IsHard) continue;
			displayedFissures.Add(fissure);
		}

		var activeIds = GameData.fissures.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
		bool pruned = notifiedFissures.RemoveWhere(id => !activeIds.Contains(id)) > 0;

		if (matches.Count > 0 || pruned) {
			await SaveNotifiedFissures();
		}

		if (matches.Count == 0) return;

		var title = matches.Count == 1 ? "Fissure found" : $"{matches.Count} Fissures found";
		var body = matches.Count == 1
			? $"{(matches[0].IsHard ? "Steel Path" : "Normal")} {matches[0].MissionType} {matches[0].Tier} ends in {matches[0].TimeRemaining}"
			: string.Join(Environment.NewLine, matches.Select(f => $"{(f.IsHard ? "Steel Path" : "Normal")} {f.MissionType} {f.Tier} ends in {f.TimeRemaining}"));

		_ = ToastWindow.ShowToastAsync(this, title, body, TimeSpan.FromSeconds(10));
	}

	private async Task LoadNotifiedFissures()
	{
		try {
			if (!File.Exists(notifiedFissuresFile)) return;

			var lines = await File.ReadAllLinesAsync(notifiedFissuresFile);
			foreach (var line in lines) {
				var id = line.Trim();
				if (!string.IsNullOrWhiteSpace(id)) {
					notifiedFissures.Add(id);
				}
			}
		} catch {
			MessageBox.Show(this, "Error", "Failed to load seen fissures list.");
		}
	}

	private async Task SaveNotifiedFissures()
	{
		try {
			var lines = notifiedFissures.OrderBy(x => x, StringComparer.Ordinal).ToArray();
			await File.WriteAllLinesAsync(notifiedFissuresFile, lines);
		} catch {
			MessageBox.Show(this, "Error", "Failed to save seen fissures list.");
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

	private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
	{
		searchDebounce?.Cancel();
		searchDebounce?.Dispose();
		searchDebounce = new CancellationTokenSource();

		var token = searchDebounce.Token;
		searchText = ItemsSearchBox.Text ?? string.Empty;

		try {
			await Task.Delay(TimeSpan.FromMilliseconds(200), token);
		} catch (OperationCanceledException) {
			return;
		}

		if (token.IsCancellationRequested) return;
		RefreshItemsList();
	}

	private async void OnFissureFilterClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button button) return;

		FissureFilterNormalButton.Classes.Set("selected", false);
		FissureFilterSteelPathButton.Classes.Set("selected", false);
		button.Classes.Set("selected", true);
		currentFissureFilter = button.Tag?.ToString() ?? "Normal";
		await RefreshFissuresList();
	}

	private async void OnRefreshFissuresClick(object? sender, RoutedEventArgs e)
	{
		await VoidFissure.LoadVoidFissures(this);
		await RefreshFissuresList();
	}

	private async void OnRefreshInfoClickAsync(object? sender, RoutedEventArgs e)
	{
		await GameData.ExtractGameInfo(this);
		await ParseInfo();
	}

	private async void OnFilterListClick(object? sender, RoutedEventArgs e)
	{
		try {
			var filterWindow = new FissuresAlert();
			await filterWindow.ShowDialog(this);
			loadedFissureAlertList = FissureAlertList.Load();
			await RefreshFissuresList();
		} catch (Exception ex) {
			MessageBox.Show(this, "Error", "Failed to open Fissures Alert Settings: " + ex.Message);
		}
	}
}