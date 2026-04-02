using Avalonia.Media.Imaging;
using System.Collections.ObjectModel;
using System.IO;

namespace framenion.Src;

public class Item(string name, string type, ObservableCollection<RecipeIngredient> ingredients, string category, string iconPath, bool mastered)
{
	public string Name { get; set; } = name;
	public string Type { get; set; } = type;
	public Bitmap? Icon { get; } = File.Exists(iconPath) ? GameData.GetOrCreateBitmap(iconPath) : null;
	public ObservableCollection<RecipeIngredient> Ingredients { get; set; } = ingredients;
	public string Category { get; set; } = category;
	public string BorderColor { get; set; } = "#4A4A4A";
	public bool Mastered { get; set; } = mastered;
}

public class RecipeIngredient(string name, string type, int count, string iconPath)
{
	public string RecipeKey { get; set; } = string.Empty;
	public string Name { get; set; } = name;
	public string ItemType { get; set; } = type;
	public int Count { get; set; } = count;
	public int OwnedCount { get; set; }
	public string CountName { get; set; } = $"{count}x {name}";
	public string BackgroundColor => OwnedCount >= Count ? "#207a35" : "#252525";
	public string BorderColor { get; set; } = "#4A4A4A";
	public bool IsCountVisible => OwnedCount > 0;
	public Bitmap? Icon { get; set; } = File.Exists(iconPath)? GameData.GetOrCreateBitmap(iconPath) : null;
}