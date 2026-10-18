using Avalonia.Media.Imaging;
using System.Collections.ObjectModel;

namespace framenion.Src;

public class Item(string name, string type, ObservableCollection<RecipeIngredient> ingredients, string category, string iconPath, bool mastered, string price)
{
	public string Name { get; set; } = name;
	public string Type { get; set; } = type;
	public string IconPath { get; set; } = iconPath;

	// Decoded on first binding read rather than in the constructor: the catalogue holds
	// thousands of items and the grid only ever shows a screenful.
	public Bitmap? Icon => GameData.GetOrCreateBitmap(IconPath);

	public ObservableCollection<RecipeIngredient> Ingredients { get; set; } = ingredients;
	public string Category { get; set; } = category;
	public string BorderColor { get; set; } = "#4A4A4A";
	public bool Mastered { get; set; } = mastered;
	public string Price { get; set; } = price;

	public bool IsPriceVisible => !string.IsNullOrEmpty(Price);
}

public class RecipeIngredient(string name, string type, int count, string iconPath, string price = "", string ducats = "")
{
	public string RecipeKey { get; set; } = string.Empty;
	public string Name { get; set; } = name;
	public string ItemType { get; set; } = type;
	public string IconPath { get; set; } = iconPath;
	public int Count { get; set; } = count;
	public int OwnedCount { get; set; } = 0;
	public string CountName { get; set; } = $"{(count > 1 ? $"{count}x " : "")}{name}";
	public string BorderColor { get; set; } = "#4A4A4A";

	public Bitmap? Icon => GameData.GetOrCreateBitmap(IconPath);

	public string Price { get; set; } = price;
	public string Ducats { get; set; } = ducats;

	public string BackgroundColor => OwnedCount >= Count ? "#207a35" : "#252525";
	public bool IsCountVisible => OwnedCount > 0;
	public bool IsPriceVisible => !string.IsNullOrEmpty(Price) || !string.IsNullOrEmpty(Ducats);
}
