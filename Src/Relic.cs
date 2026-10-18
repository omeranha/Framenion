using Avalonia.Media.Imaging;
using System.Collections.ObjectModel;

namespace framenion.Src;

public class Relic(string name, string type, string iconPath, ObservableCollection<Reward> rewards)
{
	public string Name { get; set; } = name;
	public string Type { get; set; } = type;
	public string IconPath { get; set; } = iconPath;

	public Bitmap? Icon => GameData.GetOrCreateBitmap(IconPath);

	public string Era { get; set; } = "";
	public string Quality { get; set; } = "";
	public ObservableCollection<Reward> Rewards { get; set; } = rewards;
	public int OwnedCount { get; set; } = 0;
	public bool IsCountVisible => OwnedCount > 0;
	public bool Unowned => OwnedCount == 0;
}

public class Reward(string name, string type, string iconPath, int count, string rarity, string borderColor)
{
	public string Name { get; set; } = name;
	public string Type { get; set; } = type;
	public string IconPath { get; set; } = iconPath;

	public Bitmap? Icon => GameData.GetOrCreateBitmap(IconPath);

	public int Count { get; set; } = count;
	public string Rarity { get; set; } = rarity;
	public string BorderColor { get; set; } = borderColor;

	public string CountName => Count > 1 ? $"{Count}x {Name}" : Name;
}
