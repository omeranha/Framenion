using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using framenion.Src;
using framenion.Src.Ocr;
using framenion.Src.Riven;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace framenion;

public partial class MainWindow : Window
{
	private readonly BulkObservableCollection<Item> displayedItems = [];
	private readonly BulkObservableCollection<Relic> displayedRelics = [];
	private readonly BulkObservableCollection<OwnedRiven> displayedRivens = [];
	private readonly BulkObservableCollection<VoidFissure> displayedFissures = [];

	private string searchText = string.Empty;
	private string searchRelicsText = string.Empty;
	private string searchRivensText = string.Empty;
	private string currentItemsFilter = "All";
	private string currentRelicsFilter = "All";
	private string currentRivensFilter = "All";
	private string currentFissureFilter = "Normal";
	private bool hideUnownedRelics;

	/// <summary>Sort orders offered on the relics tab.</summary>
	public List<string> RelicSortOptions { get; } = ["Era", "Name", "Owned count", "Refinement"];

	/// <summary>Sort orders offered on the rivens tab.</summary>
	public List<string> RivenSortOptions { get; } = ["Grade", "Weapon", "Disposition", "Rolls", "Mastery rank"];

	private readonly string notifiedFissuresFile = Path.Combine(AppData.AppDataDir, "notified_fissures.txt");

	private readonly HashSet<string> notifiedFissures = [];
	private IReadOnlyList<FissureAlertEntry> loadedFissureAlertList = [];

	public DispatcherTimer fissureRefreshTimer = new() { Interval = TimeSpan.FromMinutes(2) };
	public DispatcherTimer fissureUpdateTimer = new() { Interval = TimeSpan.FromSeconds(1) };

	private readonly Debouncer itemsSearchDebounce = new();
	private readonly Debouncer relicsSearchDebounce = new();
	private readonly Debouncer rivensSearchDebounce = new();

	private double itemsZoom = 1.0;
	private const double ItemsZoomMin = 0.50;
	private const double ItemsZoomMax = 2.00;
	private const double ItemsZoomStep = 0.10;

	// Styled properties rather than a hand-rolled INotifyPropertyChanged: Window already
	// implements that interface, and shadowing it with a `new` event breaks notifications.
	public static readonly StyledProperty<double> ItemsTileWidthProperty =
		AvaloniaProperty.Register<MainWindow, double>(nameof(ItemsTileWidth), 200d);

	public static readonly StyledProperty<double> ItemsTileMinHeightProperty =
		AvaloniaProperty.Register<MainWindow, double>(nameof(ItemsTileMinHeight), 245d);

	public static readonly StyledProperty<double> ItemsIconSizeProperty =
		AvaloniaProperty.Register<MainWindow, double>(nameof(ItemsIconSize), 72d);

	public static readonly StyledProperty<bool> IsLoadingProperty =
		AvaloniaProperty.Register<MainWindow, bool>(nameof(IsLoading));

	public double ItemsTileWidth
	{
		get => GetValue(ItemsTileWidthProperty);
		set => SetValue(ItemsTileWidthProperty, value);
	}

	public double ItemsTileMinHeight
	{
		get => GetValue(ItemsTileMinHeightProperty);
		set => SetValue(ItemsTileMinHeightProperty, value);
	}

	public double ItemsIconSize
	{
		get => GetValue(ItemsIconSizeProperty);
		set => SetValue(ItemsIconSizeProperty, value);
	}

	public bool IsLoading
	{
		get => GetValue(IsLoadingProperty);
		set => SetValue(IsLoadingProperty, value);
	}

	private readonly WindowsKeyboardHook keyboardHook = new();
	private Action<bool>? onProcessStateChanged;
	private Action? onSelectionClosed;

	public MainWindow()
	{
		Directory.CreateDirectory(AppData.CacheDir);
		Directory.CreateDirectory(AppData.IconsCacheDir);
		InitializeComponent();
		if (Design.IsDesignMode) {
			return;
		}

		DataContext = this;
		// Bound once; refreshes replace the contents in place.
		ItemsList.ItemsSource = displayedItems;
		RelicsList.ItemsSource = displayedRelics;
		RivensList.ItemsSource = displayedRivens;
		FissuresList.ItemsSource = displayedFissures;
		WireNotifications();
		WireMonitorEvents();
		_ = InitializeAsync();
	}

	private static void WireNotifications()
	{
		Notify.Toast = (title, body) => ToastWindow.Show(title, body);
		Notify.Error = (title, body) => MessageBox.Show(title, body);
		Notify.Confirm = (title, body, ok, cancel) => MessageBox.AskYesNo(title, body, ok, cancel);
	}

	private void WireMonitorEvents()
	{
		if (AppData.Monitor == null) return;

		onProcessStateChanged = running => Dispatcher.UIThread.Post(() => {
			if (running) {
				WarframeOpened.Source = GameData.GetOrCreateBitmap(Path.Combine(AppContext.BaseDirectory, "assets", "check_d.png"));
				ToolTip.SetTip(WarframeOpened, "Warframe is up and running.");
				if (AppData.AppSettings.EnableEELogRead) {
					AppData.Monitor.Start();
				}
			} else {
				WarframeOpened.Source = GameData.GetOrCreateBitmap(Path.Combine(AppContext.BaseDirectory, "assets", "uncheck_d.png"));
				ToolTip.SetTip(WarframeOpened, "Warframe is closed");
				AppData.Monitor.Stop();
			}
		});

		onSelectionClosed = () => Dispatcher.UIThread.Post(() => {
			foreach (var win in AppData.RewardWindows.ToArray()) {
				if (win.IsVisible) win.Close();
			}
		});

		AppData.Monitor.OnProcessStateChanged += onProcessStateChanged;
		AppData.Monitor.OnRewardDetected += RelicRewardOCR.ReadRelicWindow;
		AppData.Monitor.OnSelectionClosed += onSelectionClosed;
		AppData.Monitor.OnRivenSessionOpen += RivenReader.OnSessionOpen;
		AppData.Monitor.OnRivenRolled += RivenReader.OnRolled;
		AppData.Monitor.OnRivenSessionClose += RivenReader.OnSessionClose;
	}

	/// <summary>
	/// The close button hides to the tray rather than quitting; Exit in the tray menu is the
	/// only way out.
	/// </summary>
	protected override void OnClosing(WindowClosingEventArgs e)
	{
		if (!AppData.IsShuttingDown) {
			e.Cancel = true;
			Hide();
			ToastWindow.Show("Framenion", "Running in the system tray");
			return;
		}

		base.OnClosing(e);
	}

	protected override void OnClosed(EventArgs e)
	{
		base.OnClosed(e);

		if (AppData.Monitor != null) {
			if (onProcessStateChanged != null) AppData.Monitor.OnProcessStateChanged -= onProcessStateChanged;
			AppData.Monitor.OnRewardDetected -= RelicRewardOCR.ReadRelicWindow;
			if (onSelectionClosed != null) AppData.Monitor.OnSelectionClosed -= onSelectionClosed;
			AppData.Monitor.OnRivenSessionOpen -= RivenReader.OnSessionOpen;
			AppData.Monitor.OnRivenRolled -= RivenReader.OnRolled;
			AppData.Monitor.OnRivenSessionClose -= RivenReader.OnSessionClose;
		}

		RivenOverlayWindow.CloseOverlay();

		foreach (var win in AppData.RewardWindows.ToArray()) {
			win.Close();
		}

		keyboardHook.Unhook();
		fissureRefreshTimer?.Stop();
		fissureUpdateTimer?.Stop();
		AppData.OcrEngine?.Dispose();
		itemsSearchDebounce.Dispose();
		relicsSearchDebounce.Dispose();
		AppData.Monitor?.Dispose();
		GameData.ClearBitmapCache();

		if (Application.Current is { } app) {
			var icons = TrayIcon.GetIcons(app);
			if (icons != null) {
				foreach (var icon in icons) {
					(icon as IDisposable)?.Dispose();
				}
				icons.Clear();
			}
		}
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);
		if (change.Property == WindowStateProperty && change.NewValue is WindowState newState && newState == WindowState.Minimized) {
			Hide();
			ToastWindow.Show("Framenion", "Application minimized to system tray");
		}
	}

	private async Task InitializeAsync()
	{
		IsLoading = true;
		try {
			await LoadNotifiedFissures();
			loadedFissureAlertList = FissureAlertList.Load();

			fissureRefreshTimer.Tick += OnFissureRefreshTick;
			fissureUpdateTimer.Tick += (_, _) => UpdateFissureTimers();
			fissureUpdateTimer.Start();

			AppData.OcrEngine = await OcrEngineFactory.CreateAsync();
			Log.Info($"OCR engine: {AppData.OcrEngine?.Name ?? "unavailable"}");

			await LoadDataAsync();

			// Only start polling once the region table is in place; a tick before that would
			// drop every fissure as an unknown node.
			fissureRefreshTimer.Start();
		} catch (Exception ex) {
			Log.Error("Initialization failed.", ex);
			MessageBox.Show("Error", $"Failed to initialize application: {ex.Message}\n\nSee {Log.Path}");
		} finally {
			IsLoading = false;
			HookHotkey();
		}
	}

	private void HookHotkey()
	{
		try {
			keyboardHook.KeyEvent += key => {
				switch (key) {
					case Avalonia.Input.Key.PrintScreen:
						RelicRewardOCR.ReadRelicWindow();
						RivenReader.Rescan();
						break;
				}
			};
			keyboardHook.Hook();
		} catch (Exception ex) {
			Log.Warn("Could not install the global hotkeys; the overlays can still be triggered by the game log.", ex);
		}
	}

	/// <summary>
	/// Downloads, parses and indexes the export on a worker thread. All of this used to run on
	/// the dispatcher, which froze the window for the whole of startup.
	/// </summary>
	private async Task LoadDataAsync()
	{
		try {
			await Task.Run(async () => {
				await PublicExport.SyncAsync();
				await PublicExport.LoadAsync();
				GameData.BuildIndexes();
				await GameData.LoadWFMarketData(updateFile: false);
				await GameData.BuildCatalogAsync();
			});

			await VoidFissure.LoadVoidFissures();
			await RefreshFissuresList();

			var summary = await Task.Run(Inventory.LoadAsync);
			ApplyInventorySummary(summary);
			RefreshItemsList();
			RefreshRelicsList();
			RefreshRivensList();
		} catch (Exception ex) {
			Log.Error("Failed to load game data.", ex);
			MessageBox.Show("Error", $"Failed to load game data: {ex.Message}\n\nSee {Log.Path}");
		}
	}

	private void ApplyInventorySummary(InventorySummary? summary)
	{
		if (summary == null) return;

		PlatinumText.Text = summary.Platinum;
		CreditsText.Text = summary.Credits;
		MasteryText.Text = summary.Mastery;

		// Routed through the shared cache so repeated refreshes do not leak a bitmap each time.
		var mastery = GameData.GetOrCreateBitmap(summary.MasteryIconPath, 64);
		if (mastery != null) MasteryIcon.Source = mastery;

		var player = GameData.GetOrCreateBitmap(summary.PlayerIconPath, 64);
		if (player != null) PlayerIcon.Source = player;
	}

	private async void OnFissureRefreshTick(object? sender, EventArgs e)
	{
		try {
			await VoidFissure.LoadVoidFissures();
			await RefreshFissuresList();
		} catch (Exception ex) {
			Log.Error("Fissure refresh failed.", ex);
		}
	}

	private void UpdateFissureTimers()
	{
		var now = DateTime.UtcNow;

		// Prune the master list, not just the visible one: filtered-out fissures used to linger
		// forever and reappear expired when the filter changed.
		bool expired = false;
		bool notifiedChanged = false;
		for (int i = GameData.Fissures.Count - 1; i >= 0; i--) {
			var fissure = GameData.Fissures[i];
			if (fissure.Expiry > now) continue;

			GameData.Fissures.RemoveAt(i);
			notifiedChanged |= notifiedFissures.Remove(fissure.Id);
			expired = true;
		}

		if (expired) {
			for (int i = displayedFissures.Count - 1; i >= 0; i--) {
				if (displayedFissures[i].Expiry <= now) displayedFissures.RemoveAt(i);
			}
		}

		foreach (var fissure in displayedFissures) {
			fissure.UpdateTimeRemaining();
		}

		if (notifiedChanged) _ = SaveNotifiedFissures();
	}

	private void RefreshItemsList()
	{
		// GameData.ItemsList is sorted once when the catalogue is built, so filtering is all
		// that is left to do per keystroke.
		IEnumerable<Item> filtered = GameData.ItemsList;
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
			var query = searchText.Trim();
			filtered = filtered.Where(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
		}

		// Assigning in bulk raises a single Reset instead of a change per item.
		displayedItems.Replace(filtered);
	}

	private void RefreshRelicsList()
	{
		IEnumerable<Relic> filtered = GameData.RelicList;
		if (currentRelicsFilter != "All") {
			filtered = filtered.Where(r => string.Equals(r.Era, currentRelicsFilter, StringComparison.OrdinalIgnoreCase));
		}

		if (hideUnownedRelics) {
			filtered = filtered.Where(r => r.OwnedCount > 0);
		}

		if (!string.IsNullOrWhiteSpace(searchRelicsText)) {
			var query = searchRelicsText.Trim();
			filtered = filtered.Where(r =>
				r.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
				r.Rewards.Any(reward => reward.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
		}

		// GameData.RelicList is already in era order, so that case needs no re-sort.
		filtered = (RelicSortBox?.SelectedItem as string) switch {
			"Name" => filtered.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => GameData.GetRelicSortKey(r.Quality)),
			"Owned count" => filtered.OrderByDescending(r => r.OwnedCount)
				.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
				.ThenBy(r => GameData.GetRelicSortKey(r.Quality)),
			"Refinement" => filtered.OrderBy(r => GameData.GetRelicSortKey(r.Quality))
				.ThenBy(r => VoidFissure.GetTierSortKey(r.Era))
				.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
			_ => filtered,
		};

		displayedRelics.Replace(filtered);
	}

	private void OnToggleUnownedRelicsClick(object? sender, RoutedEventArgs e)
	{
		hideUnownedRelics = HideUnownedRelicsButton.IsChecked ?? false;
		ToolTip.SetTip(HideUnownedRelicsButton, hideUnownedRelics ? "Show relics you do not own" : "Hide relics you do not own");
		RefreshRelicsList();
	}

	/// <summary>
	/// Rivens come from the inventory dump, not the export, so this list is empty until the
	/// dump has been read; the count label doubles as the "nothing here" explanation.
	/// </summary>
	private void RefreshRivensList()
	{
		IEnumerable<OwnedRiven> filtered = GameData.RivenList;
		if (currentRivensFilter != "All") {
			filtered = filtered.Where(r => string.Equals(r.Category, currentRivensFilter, StringComparison.Ordinal));
		}

		if (!string.IsNullOrWhiteSpace(searchRivensText)) {
			var query = searchRivensText.Trim();
			filtered = filtered.Where(r =>
				r.WeaponName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
				r.RivenName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
				r.ClassName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
				r.Stats.Any(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)));
		}

		// GameData.RivenList is already in weapon order, so that case needs no re-sort.
		// Grade sorts on the roll position rather than the letter: the letter is a bucket, and
		// a B+ at 74% should still come before a B+ at 51%.
		filtered = (RivenSortBox?.SelectedItem as string) switch {
			"Grade" => filtered.OrderByDescending(r => r.StatPerfectness)
				.ThenBy(r => r.WeaponName, StringComparer.OrdinalIgnoreCase),
			"Disposition" => filtered.OrderByDescending(r => r.Disposition)
				.ThenBy(r => r.WeaponName, StringComparer.OrdinalIgnoreCase),
			"Rolls" => filtered.OrderByDescending(r => r.Rerolls)
				.ThenBy(r => r.WeaponName, StringComparer.OrdinalIgnoreCase),
			"Mastery rank" => filtered.OrderByDescending(r => r.MasteryRank)
				.ThenBy(r => r.WeaponName, StringComparer.OrdinalIgnoreCase),
			_ => filtered,
		};

		var visible = filtered.ToList();
		displayedRivens.Replace(visible);

		RivenCountText.Text = GameData.RivenList.Count == 0
			? "No rivens found — refresh the inventory"
			: visible.Count == GameData.RivenList.Count
				? $"{visible.Count} riven{(visible.Count == 1 ? "" : "s")}"
				: $"{visible.Count} of {GameData.RivenList.Count}";
	}

	private void OnFilterRivenClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button button) return;

		foreach (var other in (Button[])[FilterAllRivensButton, FilterRifleRivensButton, FilterShotgunRivensButton,
			FilterPistolRivensButton, FilterMeleeRivensButton, FilterArchgunRivensButton, FilterModularRivensButton]) {
			other.Classes.Set("selected", false);
		}

		button.Classes.Set("selected", true);
		currentRivensFilter = button.Tag?.ToString() ?? "All";
		RefreshRivensList();
	}

	private void OnRivenSortChanged(object? sender, SelectionChangedEventArgs e)
	{
		// Fires while the window is still being built, before the list exists.
		if (!IsLoaded) return;
		RefreshRivensList();
	}

	private async void OnSearchRivensTextChanged(object? sender, TextChangedEventArgs e)
	{
		searchRivensText = RivensSearchBox.Text ?? string.Empty;
		if (await rivensSearchDebounce.WaitAsync()) RefreshRivensList();
	}

	private void OnRelicSortChanged(object? sender, SelectionChangedEventArgs e)
	{
		// Fires while the window is still being built, before the list exists.
		if (!IsLoaded) return;
		RefreshRelicsList();
	}

	private async Task RefreshFissuresList()
	{
		var ordered = GameData.Fissures.Select(f => {
			f.ShouldNotify = FissureAlertList.MatchesAny(f, loadedFissureAlertList);
			return f;
		}).OrderByDescending(f => f.ShouldNotify)
		.ThenBy(f => VoidFissure.GetTierSortKey(f.Tier)).ToList();

		var matches = new List<VoidFissure>();
		var visible = new List<VoidFissure>();
		foreach (var fissure in ordered) {
			if (fissure.ShouldNotify && !notifiedFissures.Contains(fissure.Id)) {
				notifiedFissures.Add(fissure.Id);
				matches.Add(fissure);
			}

			if (currentFissureFilter == "Normal" && fissure.IsHard) continue;
			if (currentFissureFilter == "SteelPath" && !fissure.IsHard) continue;
			visible.Add(fissure);
		}
		displayedFissures.Replace(visible);

		var activeIds = GameData.Fissures.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
		bool pruned = notifiedFissures.RemoveWhere(id => !activeIds.Contains(id)) > 0;
		if (matches.Count > 0 || pruned) {
			await SaveNotifiedFissures();
		}

		if (!AppData.AppSettings.EnableNotifications || matches.Count == 0) return;

		var title = matches.Count == 1 ? "Fissure found" : $"{matches.Count} Fissures found";
		var body = string.Join(Environment.NewLine, matches.Select(f => $"{(f.IsHard ? "Steel Path" : "Normal")} {f.MissionType} {f.Tier} ends in {f.TimeRemaining}"));
		ToastWindow.Show(title, body, duration: TimeSpan.FromSeconds(8), icon: GameData.GetOrCreateBitmap(Path.Combine(AppContext.BaseDirectory, "assets", "void_fissure.png")));
	}

	private async Task LoadNotifiedFissures()
	{
		try {
			if (!File.Exists(notifiedFissuresFile)) return;

			foreach (var line in await File.ReadAllLinesAsync(notifiedFissuresFile)) {
				var id = line.Trim();
				if (!string.IsNullOrWhiteSpace(id)) notifiedFissures.Add(id);
			}
		} catch (Exception ex) {
			Log.Warn("Could not load the seen fissures list.", ex);
		}
	}

	private async Task SaveNotifiedFissures()
	{
		try {
			var lines = notifiedFissures.OrderBy(x => x, StringComparer.Ordinal).ToArray();
			await File.WriteAllLinesAsync(notifiedFissuresFile, lines);
		} catch (Exception ex) {
			Log.Warn("Could not save the seen fissures list.", ex);
		}
	}

	private void OnItemsClick(object? sender, RoutedEventArgs e) => SelectTab(ItemsButton);

	private void OnRelicsClick(object? sender, RoutedEventArgs e) => SelectTab(RelicsButton);

	private void OnRivensClick(object? sender, RoutedEventArgs e) => SelectTab(RivensButton);

	private void SelectTab(Button selected)
	{
		foreach (var (button, content) in (( Button, Border)[])[(ItemsButton, ItemsContent), (RelicsButton, RelicsContent), (RivensButton, RivensContent)]) {
			bool active = ReferenceEquals(button, selected);
			button.Classes.Set("selected", active);
			content.IsVisible = active;
		}
	}

	private void OnFilterClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button button) return;

		foreach (var other in (Button[])[FilterAllButton, FilterWarframesButton, FilterPrimaryButton, FilterSecondaryButton,
			FilterMeleeButton, FilterArchgunsButton, FilterArchmeleeButton, FilterVehiclesButton, FilterCompanionsButton, FilterAmpsButton]) {
			other.Classes.Set("selected", false);
		}

		button.Classes.Set("selected", true);
		currentItemsFilter = button.Tag?.ToString() ?? "All";
		RefreshItemsList();
	}

	private void OnFilterRelicClick(object? sender, RoutedEventArgs e)
	{
		if (sender is not Button button) return;

		foreach (var other in (Button[])[FilterAllRelicsButton, FilterLithButton, FilterMesoButton, FilterNeoButton, FilterAxiButton, FilterRequiemButton]) {
			other.Classes.Set("selected", false);
		}

		button.Classes.Set("selected", true);
		currentRelicsFilter = button.Tag?.ToString() ?? "All";
		RefreshRelicsList();
	}

	private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
	{
		searchText = ItemsSearchBox.Text ?? string.Empty;
		if (await itemsSearchDebounce.WaitAsync()) RefreshItemsList();
	}

	private async void OnSearchRelicsTextChanged(object? sender, TextChangedEventArgs e)
	{
		searchRelicsText = RelicsSearchBox.Text ?? string.Empty;
		if (await relicsSearchDebounce.WaitAsync()) RefreshRelicsList();
	}

	/// <summary>
	/// Collapses a burst of keystrokes into one refresh. Each search box owns an instance; a
	/// single shared token source let typing in one box cancel the other's pending refresh.
	/// </summary>
	private sealed class Debouncer : IDisposable
	{
		private static readonly TimeSpan delay = TimeSpan.FromMilliseconds(200);

		private CancellationTokenSource source = new();

		public async Task<bool> WaitAsync()
		{
			var replacement = new CancellationTokenSource();
			var previous = Interlocked.Exchange(ref source, replacement);
			try {
				previous.Cancel();
				previous.Dispose();
			} catch (ObjectDisposedException) {
				// Already torn down.
			}

			try {
				await Task.Delay(delay, replacement.Token);
				return true;
			} catch (OperationCanceledException) {
				return false;
			}
		}

		public void Dispose()
		{
			try {
				source.Cancel();
				source.Dispose();
			} catch (ObjectDisposedException) {
				// Already torn down.
			}
		}
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
		try {
			await VoidFissure.LoadVoidFissures();
			await RefreshFissuresList();
		} catch (Exception ex) {
			Log.Error("Manual fissure refresh failed.", ex);
		}
	}

	private async void OnRefreshInfoClickAsync(object? sender, RoutedEventArgs e)
	{
		try {
			IsLoading = true;
			await GameData.ExtractGameInfo();
			var summary = await Task.Run(Inventory.LoadAsync);
			ApplyInventorySummary(summary);
			RefreshItemsList();
			RefreshRelicsList();
			RefreshRivensList();
		} catch (Exception ex) {
			Log.Error("Inventory refresh failed.", ex);
			MessageBox.Show("Error", $"Failed to refresh inventory: {ex.Message}\n\nSee {Log.Path}");
		} finally {
			IsLoading = false;
		}
	}

	private async void OnFilterListClick(object? sender, RoutedEventArgs e)
	{
		try {
			var filterWindow = new FissuresAlert();
			await filterWindow.ShowDialog(this);
			loadedFissureAlertList = FissureAlertList.Load();
			await RefreshFissuresList();
		} catch (Exception ex) {
			Log.Error("Could not open the fissure alert settings.", ex);
			MessageBox.Show("Error", "Failed to open Fissures Alert Settings: " + ex.Message);
		}
	}

	private async void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
	{
		try {
			await new SettingsWindow().ShowDialog(this);
		} catch (Exception ex) {
			Log.Error("Could not open settings.", ex);
			MessageBox.Show("Error", "Failed to open Settings: " + ex.Message);
		}
	}

	private void SetItemsZoom(double newZoom)
	{
		newZoom = Math.Clamp(newZoom, ItemsZoomMin, ItemsZoomMax);
		if (Math.Abs(newZoom - itemsZoom) < 0.0001) return;

		itemsZoom = newZoom;
		ItemsTileWidth = Math.Round(200 * itemsZoom);
		ItemsTileMinHeight = Math.Round(245 * itemsZoom);
		// The icon used to stay at a fixed size, so zooming only added whitespace.
		ItemsIconSize = Math.Round(72 * itemsZoom);

		ItemsList.InvalidateMeasure();
		ItemsList.InvalidateArrange();
		RelicsList.InvalidateMeasure();
		RelicsList.InvalidateArrange();
	}

	private void OnItemsZoomOutClick(object? sender, RoutedEventArgs e) => SetItemsZoom(itemsZoom - ItemsZoomStep);

	private void OnItemsZoomResetClick(object? sender, RoutedEventArgs e) => SetItemsZoom(1.0);

	private void OnItemsZoomInClick(object? sender, RoutedEventArgs e) => SetItemsZoom(itemsZoom + ItemsZoomStep);
}
