using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using framenion.Src;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace framenion;

public partial class MainWindow : Window, INotifyPropertyChanged
{
	public ObservableCollection<Item> displayedItems = [];
	public ObservableCollection<VoidFissure> displayedFissures = [];

	private string searchText = string.Empty;
	private string currentItemsFilter = "All";
	private string currentFissureFilter = "Normal";

	private static readonly string EElog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Warframe/EE.log");
	private static readonly string inventoryFile = Path.Combine(GameData.appDataDir, "inventory.json");
	private static readonly string notifiedFissuresFile = Path.Combine(GameData.appDataDir, "notified_fissures.txt");

	private readonly HashSet<string> notifiedFissures = [];

	private IReadOnlyList<FissureAlertEntry> loadedFissureAlertList = [];

	private CancellationTokenSource? searchDebounce;
	private long lastPosition = 0;
	private bool lastWfRunning = false;

	private double itemsZoom = 1.0;
	private const double ItemsZoomMin = 0.50;
	private const double ItemsZoomMax = 2.00;
	private const double ItemsZoomStep = 0.10;
	
	public double ItemsTileWidth => Math.Round(200 * itemsZoom);
	public double ItemsTileMinHeight => Math.Round(220 * itemsZoom);

	public new event PropertyChangedEventHandler? PropertyChanged;
	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	private bool isLoading;
	public bool IsLoading
	{
		get => isLoading;
		set {
			if (isLoading != value) {
				isLoading = value;
				OnPropertyChanged();
			}
		}
	}

	private readonly WindowsKeyboardHook keyboardHook = new();

	public MainWindow()
	{
		Directory.CreateDirectory(GameData.cacheDir);
		Directory.CreateDirectory(GameData.iconsCacheDir);
		InitializeComponent();
		if (Design.IsDesignMode) {
			return;
		}
		Opened += async (s, e) => {
			_ = InitializeAsync();
		};
		DataContext = this;
		ItemsList.ItemsSource = displayedItems;
		FissuresList.ItemsSource = displayedFissures;
	}

	protected override void OnClosed(EventArgs e)
	{
		base.OnClosed(e);
		keyboardHook.Unhook();
		GameData.fissureRefreshTimer?.Stop();
		GameData.fissureUpdateTimer?.Stop();
		GameData.logPollTimer?.Stop();
		if (GameData.paddleEngine is IDisposable disposableEngine) {
			disposableEngine.Dispose();
		}
		searchDebounce?.Cancel();
		searchDebounce?.Dispose();
	}

	private async Task InitializeAsync()
	{
		IsLoading = true;
		try {
			GameData.appSettings = await AppSettings.LoadAsync();
			await LoadNotifiedFissures();
			loadedFissureAlertList = FissureAlertList.Load();

			GameData.fissureRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
			GameData.fissureRefreshTimer.Tick += async (_, _) => {
				await VoidFissure.LoadVoidFissures(this);
				await RefreshFissuresList();
			};
			GameData.fissureUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
			GameData.fissureUpdateTimer.Tick += (_, _) => UpdateFissureTimers();
			GameData.fissureUpdateTimer.Start();
			GameData.fissureRefreshTimer.Start();

			GameData.logPollTimer.Tick += (_, _) => ReadEELog();
			if (GameData.appSettings.EnableEELogRead) {
				GameData.logPollTimer.Start();
			}

			FullOcrModel model = await OnlineFullModels.EnglishV4.DownloadAsync();
			GameData.paddleEngine = new(model, PaddleDevice.PlatformDefault) {
				AllowRotateDetection = false,
				Enable180Classification = false,
			};
			await InitializeDataInBackgroundAsync();
		} catch (Exception ex) {
			MessageBox.Show(this, "Error", "Failed to initialize application: " + ex.Message);
		} finally {
			IsLoading = false;
			keyboardHook.KeyEvent += key => {
				if (key == Avalonia.Input.Key.PrintScreen) {
					Dispatcher.UIThread.Post(() => {
						try {
							ReadRelicWindow(false);
						} catch (Exception ex) {
							MessageBox.Show(this, "Error", $"Failed to read rewards: {ex.Message}");
						}
					});
				}
			};
			keyboardHook.Hook();
		}
	}

	private async Task InitializeDataInBackgroundAsync()
	{
		try {
			string hashPath = Path.Combine(GameData.cacheDir, "export_hash");
			GameData.httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Framenion");
			var resp = await GameData.httpClient.GetStringAsync("https://api.github.com/repos/calamity-inc/warframe-public-export-plus/commits/senpai");
			using var json = JsonDocument.Parse(resp);
			string latestHash = json.RootElement.GetProperty("sha").GetString() ?? "";

			string localHash =  "";
			if (File.Exists(hashPath)) {
				localHash = (await File.ReadAllTextAsync(hashPath)).Trim();
			}

			bool needsUpdate = !string.IsNullOrEmpty(latestHash) && !string.Equals(localHash.Trim(), latestHash, StringComparison.Ordinal);
			if (needsUpdate) {
				await File.WriteAllTextAsync(hashPath, latestHash);
				ToastWindow.ShowToast(this, "Data initialization", "A new update has been found, updating cache");
			}

			string[] firstLoad = ["dict.en", "ExportRegions", "ExportMissionTypes", "ExportFactions"];
			var loadTasks = firstLoad.Select(f => GameData.LoadFile(this, f, GameData.cacheDir, needsUpdate));
			await Task.WhenAll(loadTasks);
			await Dispatcher.UIThread.InvokeAsync(async () => {
				await VoidFissure.LoadVoidFissures(this);
				await RefreshFissuresList();
			});
			string[] secondLoad = ["ExportWarframes", "ExportRecipes", "ExportWeapons", "ExportResources", "ExportMisc", "ExportSentinels", "ExportTextIcons"];
			loadTasks = secondLoad.Select(f => GameData.LoadFile(this, f, GameData.cacheDir, needsUpdate));
			await Task.WhenAll(loadTasks);
			await GameData.LoadWFMarketData(this, needsUpdate);

			await GameData.LoadExports(this);
			await ParseInfo();
		} catch (Exception e) {
			MessageBox.Show(this, "Error", "Failed to initialize data in background: " + e.Message);
		}
	}

	private async Task ParseInfo()
	{
		if (!File.Exists(inventoryFile)) {
			return;
		}

		string playerName = "";
		string platinumText = "";
		string creditsText = "";
		string masteryText = "";
		Bitmap? masteryIcon = null;
		Bitmap? playerIcon = null;

		await using var stream = File.OpenRead(inventoryFile);
		using var doc = await JsonDocument.ParseAsync(stream);
		var root = doc.RootElement;
		if (root.ValueKind != JsonValueKind.Object) return;
		GameData.mainRoot = root;

		if (GameData.appSettings.EnableEELogRead) {
			var loggedInName = ReadAccountName();
			if (!string.IsNullOrWhiteSpace(loggedInName)) {
				playerName = loggedInName;
			}
		}

		if (root.TryGetProperty("PremiumCredits", out var platinum) && platinum.TryGetInt64(out var p)) {
			platinumText = p.ToString("N0");
		}
		if (root.TryGetProperty("RegularCredits", out var credits) && credits.TryGetInt64(out var c)) {
			creditsText = c.ToString("N0");
		}

		root.TryGetProperty("PlayerLevel", out var mr);
		var mr_str = (mr.GetUInt16() > 30) ? "L" + (mr.GetUInt16() - 30) : mr.ToString();
		masteryText = mr_str;
		if (GameData.exportTextIcons.TryGetValue($"RANK_{mr}", out var rankIconEl)) {
			using var icon_resp = await GameData.httpClient.GetAsync(rankIconEl);
			masteryIcon = new Bitmap(await icon_resp.Content.ReadAsStreamAsync());
		}

		root.TryGetProperty("ActiveAvatarImageType", out var icon_path);
		using var glyph_resp = await GameData.httpClient.GetAsync(icon_path.ToString());
		glyph_resp.EnsureSuccessStatusCode();
		using var glyph_doc = await JsonDocument.ParseAsync(await glyph_resp.Content.ReadAsStreamAsync());
		if (glyph_doc.RootElement.ValueKind == JsonValueKind.Object) {
			glyph_doc.RootElement.TryGetProperty("icon", out var icon_url);
			var icon_resp = await GameData.httpClient.GetAsync(icon_url.ToString());
			playerIcon = new Bitmap(await icon_resp.Content.ReadAsStreamAsync());
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

		var miscByType = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var e in miscEl.EnumerateArray())
			if (e.TryGetProperty("ItemType", out var t) && t.GetString() is string tStr)
				miscByType[tStr] = e.GetProperty("ItemCount").GetInt32();

		var recipesByType = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var e in recipesEl.EnumerateArray()) {
			if (e.TryGetProperty("ItemType", out var t) && t.GetString() is string tStr) {
				recipesByType[tStr] = e.GetProperty("ItemCount").GetInt32();
			}
		}

		foreach (var item in GameData.itemsList) {
			var resultType = item.Type;

			xpByType.TryGetValue(resultType, out var xpForItem);
			var requiredXp = XpToMaster(resultType);
			if (xpForItem >= requiredXp) item.Mastered = true;
			if (ownedTypes.Contains(resultType)) item.BorderColor = "#3aba29";

			foreach (var ingred in item.Ingredients) {
				var type = ingred.ItemType;
				if (!miscByType.TryGetValue(type, out var ingred_count) && !recipesByType.TryGetValue(ingred.RecipeKey, out ingred_count)) {
					continue;
				}

				ingred.OwnedCount = ingred_count;
				if (!GameData.exportRecipes.TryGetValue(type, out var subRecipe) || subRecipe.recipe.Ingredients == null) continue;
				var subIngredients = subRecipe.recipe.Ingredients;
				bool canCraft = true;
				foreach (var sub in subIngredients) {
					if (!miscByType.TryGetValue(sub.Type, out var subEntry) || subEntry < sub.Count) {
						canCraft = false;
						break;
					}
				}

				if (canCraft && ingred.BackgroundColor == "#252525") ingred.BorderColor = "#4A9EFF";
			}

			if (item.Ingredients.Count > 0 && item.Ingredients.All(i => i.OwnedCount >= i.Count)) item.BorderColor = "#4A9EFF";

			await Dispatcher.UIThread.InvokeAsync(() => {
				if (!string.IsNullOrWhiteSpace(playerName)) PlayerName.Text = playerName;
				PlatinumText.Text = platinumText;
				CreditsText.Text = creditsText;
				MasteryText.Text = masteryText;

				if (masteryIcon != null) {
					MasteryIcon.Source = masteryIcon;
				}
				if (playerIcon != null) {
					PlayerIcon.Source = playerIcon;
				}
				RefreshItemsList();
			});
		}
	}

	private static long XpToMaster(string type)
	{
		int levelCap = 30;
		if (GameData.uniquelevelCaps.Contains(type)) {
			levelCap = 40;
		}

		bool isWarframe = type.Contains("/Lotus/Powersuits/", StringComparison.Ordinal);
		long baseXp = isWarframe ? 1000L : 500L;
		return baseXp * (long)levelCap * (long)levelCap;
	}

	private static string ReadAccountName()
	{
		if (!File.Exists(EElog)) return "";

		const string marker = "Logged in ";
		var fileInfo = new FileInfo(EElog);
		long fileLength = fileInfo.Length;
		using var fs = new FileStream(EElog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
		using var stream = mmf.CreateViewStream(0, fileLength, MemoryMappedFileAccess.Read);
		using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

		string? line;
		while ((line = reader.ReadLine()) != null) {
			int idx = line.IndexOf(marker, StringComparison.Ordinal);
			if (idx >= 0) {
				var payload = line[(idx + marker.Length)..].Trim();
				var open = payload.LastIndexOf('(');
				if (open > 0) {
					var name = payload[..open].Trim();
					if (name.Length > 0) return name.TrimEnd('-', ':').Trim();
				}
			}
		}

		return "";
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

		if (!GameData.appSettings.EnableNotifications || matches.Count == 0) return;

		var title = matches.Count == 1 ? "Fissure found" : $"{matches.Count} Fissures found";
		var body = matches.Count == 1
			? $"{(matches[0].IsHard ? "Steel Path" : "Normal")} {matches[0].MissionType} {matches[0].Tier} ends in {matches[0].TimeRemaining}"
			: string.Join(Environment.NewLine, matches.Select(f => $"{(f.IsHard ? "Steel Path" : "Normal")} {f.MissionType} {f.Tier} ends in {f.TimeRemaining}"));

		ToastWindow.ShowToast(this, title, body, TimeSpan.FromSeconds(8));
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

	private void ReadRelicWindow(bool auto)
	{
		if (!GameData.appSettings.EnableRelicOverlay) return;

		_ = Task.Run(async () => {
			try {
				if (auto) {
					await Task.Delay(2000);
				}
				var screenshot = ScreenCapture.Capture();
				var rewards = RelicRewardOCR.ReadRewards(screenshot, GameData.paddleEngine);
				if (rewards.Count == 0) {
					ToastWindow.ShowToast(this, "No rewards detected", "Could not detect any relic rewards.", TimeSpan.FromSeconds(5));
					return;
				}
				var rewardInfoTasks = rewards.Select(r => GetItemData(r.ItemName)).ToArray();
				var rewardInfos = await Task.WhenAll(rewardInfoTasks);

				var displayTasks = rewards.Zip(rewardInfos, (reward, info) => RelicRewardWindow.Display(this, info, reward.Rect.X, reward.Rect.Y + GameData.appSettings.OverlayOffset, reward.Rect.Width, TimeSpan.FromSeconds(10)));
				await Task.WhenAll(displayTasks);
			} catch (Exception ex) {
				MessageBox.Show(this, "Error", $"Failed to read rewards: {ex.Message}");
			}
		});
	}

	private async Task<RewardInfo> GetItemData(string itemName)
	{
		GameData.warframeMarketItems.TryGetValue(itemName, out var marketInfo);
		var rewardInfo = new RewardInfo(itemName);
		try {
			if (!string.IsNullOrEmpty(marketInfo.slug)) {
				rewardInfo.Ducats = marketInfo.ducats;
				var resp = await GameData.httpClient.GetAsync($"https://api.warframe.market/v2/orders/item/{marketInfo.slug}/top");
				resp.EnsureSuccessStatusCode();
				using var stream = await resp.Content.ReadAsStreamAsync();
				using var doc = await JsonDocument.ParseAsync(stream);

				if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("sell", out var sell) &&
					sell.ValueKind == JsonValueKind.Array && sell.GetArrayLength() > 0) {
					var first = sell[0];
					if (first.TryGetProperty("platinum", out var platinumProp)) {
						rewardInfo.Platinum = platinumProp.GetInt32().ToString();
					}
				}
			}
		} catch {
			MessageBox.Show(this, "Error", "Failed to fetch market data for " + itemName);
		}

		return rewardInfo;
	}

	private void ReadEELog()
	{
		if (!File.Exists(EElog)) return;

		var process = Process.GetProcessesByName("Warframe.x64").FirstOrDefault();
		bool wfRunning = process != null;
		if (lastWfRunning != wfRunning) {
			lastWfRunning = wfRunning;
			Dispatcher.UIThread.Post(() => {
				if (wfRunning) {
					WarframeOpened.Source = GameData.GetOrCreateBitmap(Path.Combine(AppContext.BaseDirectory, "assets", "check_d.png"));
					ToolTip.SetTip(WarframeOpened, "Warframe is up and running\nReading log data.");
				} else {
					WarframeOpened.Source = GameData.GetOrCreateBitmap(Path.Combine(AppContext.BaseDirectory, "assets", "uncheck_d.png"));
					ToolTip.SetTip(WarframeOpened, "Warframe is closed\nNot reading log data.");
				}
			});
		}

		if (!wfRunning) return;

		var fileInfo = new FileInfo(EElog);
		long fileLength = fileInfo.Length;
		if (lastPosition > fileLength) lastPosition = 0;
		if (lastPosition == 0 && fileLength > 0) {
			lastPosition = fileLength;
			return;
		}

		if (fileLength > lastPosition) {
			using var fs = new FileStream(EElog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
			using var accessor = mmf.CreateViewAccessor(lastPosition, fileLength - lastPosition, MemoryMappedFileAccess.Read);

			byte[] bytes = new byte[fileLength - lastPosition];
			accessor.ReadArray(0, bytes, 0, bytes.Length);

			string text = Encoding.UTF8.GetString(bytes);
			using var reader = new StringReader(text);
			string? line;
			while ((line = reader.ReadLine()) != null) {
				if (!line.Contains("Got rewards")) continue;

				ReadRelicWindow(true);
			}
			lastPosition = fileLength;
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

	private async void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
	{
		try {
			var w = new SettingsWindow();
			await w.ShowDialog(this);
		} catch (Exception ex) {
			MessageBox.Show(this, "Error", "Failed to open Settings: " + ex.Message);
		}
	}

	private void SetItemsZoom(double newZoom)
	{
		newZoom = Math.Clamp(newZoom, ItemsZoomMin, ItemsZoomMax);
		if (Math.Abs(newZoom - itemsZoom) < 0.0001) return;

		itemsZoom = newZoom;

		OnPropertyChanged(nameof(ItemsTileWidth));
		OnPropertyChanged(nameof(ItemsTileMinHeight));

		ItemsList.InvalidateMeasure();
		ItemsList.InvalidateArrange();
	}

	private void OnItemsZoomOutClick(object? sender, RoutedEventArgs e)
	{
		SetItemsZoom(itemsZoom - ItemsZoomStep);
	}

	private void OnItemsZoomResetClick(object? sender, RoutedEventArgs e)
	{
		SetItemsZoom(1.0);
	}

	private void OnItemsZoomInClick(object? sender, RoutedEventArgs e)
	{
		SetItemsZoom(itemsZoom + ItemsZoomStep);
	}
}
