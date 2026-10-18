using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using framenion.Src;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace framenion;

public partial class RelicRewardWindow : Window
{
	private static readonly TimeSpan autoCloseAfter = TimeSpan.FromSeconds(15);

	private DispatcherTimer? autoClose;

	public string ItemName { get; set; } = string.Empty;
	public string Ducats { get; set; } = string.Empty;
	public string Price { get; set; } = string.Empty;
	public ObservableCollection<RecipeIngredient> SetItems { get; set; } = [];
	public string SetName { get; set; } = string.Empty;

	public RelicRewardWindow()
	{
		InitializeComponent();
	}

	public RelicRewardWindow(string name, string price, string ducats, ObservableCollection<RecipeIngredient> setItems, string setName, int x, int y, int width)
	{
		ItemName = name;
		Ducats = ducats;
		Price = price;
		SetItems = setItems;
		SetName = setName;
		InitializeComponent();
		DataContext = this;
		Width = width;
		WindowStartupLocation = WindowStartupLocation.Manual;
		Position = new PixelPoint(x - (width / 2), y);
		IsHitTestVisible = false;
	}

	public static Task Display(string itemName, int x, int y, int width)
	{
		return Dispatcher.UIThread.InvokeAsync(() => {
			var owner = AppData.MainWindow;
			if (owner == null) return;

			// Prices are best effort: warframe.market may be unreachable, and the overlay is
			// still worth showing without them.
			GameData.WFMarketData.TryGetValue(itemName, out var market);
			GameData.DucatsByName.TryGetValue(itemName, out int ducats);
			var setItems = new ObservableCollection<RecipeIngredient>();
			string setName = string.Empty;
			if (GameData.ItemsByIngredientName.TryGetValue(itemName, out var parentItem)) {
				setName = $"{parentItem.Name} Set";
				foreach (var ingredient in parentItem.Ingredients.Where(i => !string.IsNullOrEmpty(i.Ducats))) {
					setItems.Add(ingredient);
				}
			}

			var win = new RelicRewardWindow(itemName, market?.Price ?? "", ducats > 0 ? ducats.ToString() : "", setItems, setName, x, y, width);
			try {
				win.ShowActivated = false;
			} catch (Exception ex) {
				Log.Debug($"Could not suppress activation for the reward overlay: {ex.Message}");
			}

			if (owner.IsVisible) {
				win.Show(owner);
			} else {
				win.Show();
			}

			AppData.RewardWindows.Add(win);
			win.StartAutoClose();
			win.Closed += (_, _) => {
				win.autoClose?.Stop();
				win.autoClose = null;
				AppData.RewardWindows.Remove(win);
			};
		}).GetTask();
	}

	/// <summary>A timer rather than a sleeping task, so closing early releases the window immediately.</summary>
	private void StartAutoClose()
	{
		autoClose = new DispatcherTimer { Interval = autoCloseAfter };
		autoClose.Tick += (_, _) => {
			autoClose?.Stop();
			if (IsVisible) Close();
		};
		autoClose.Start();
	}
}
