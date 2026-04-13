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
		int left = x - (width / 2);
		Position = new PixelPoint(left, y);
		IsHitTestVisible = false;
	}

	public static async Task Display(string ItemName, int x, int y, int width)
	{
		var owner = AppData.MainWindow;
		if (owner == null) return;

		await Dispatcher.UIThread.InvokeAsync(() => {
			if (!GameData.WFMarketData.TryGetValue(ItemName, out var itemData)) return;
			var parentItem = GameData.ItemsList.FirstOrDefault(i => i.Ingredients.Any(ing => string.Equals(ing.Name, ItemName, StringComparison.OrdinalIgnoreCase)));
			if (parentItem == null) return;

			var primeParts = new ObservableCollection<RecipeIngredient>(parentItem.Ingredients.Where(ing => !string.IsNullOrEmpty(ing.Ducats)).ToList());
			var win = new RelicRewardWindow(ItemName, itemData.Price, itemData.Ducats, primeParts, $"{parentItem.Name} Set", x, y, width);
			try {
				win.ShowActivated = false;
			} catch { }

			if (owner.IsVisible) {
				win.Show(owner);
			} else {
				win.Show();
			}

			AppData.RewardWindows.Add(win);
			win.Closed += (_, _) => {
				AppData.RewardWindows.Remove(win);
				RelicRewardOCR.OcrRunning = false;
			};

			_ = Task.Run(async () => {
				await Task.Delay(15000);
				await Dispatcher.UIThread.InvokeAsync(() => {
					if (win.IsVisible) win.Close();
				});
			});
		});
	}
}
