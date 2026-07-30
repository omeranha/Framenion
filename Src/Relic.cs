using Avalonia.Media.Imaging;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;

namespace framenion.Src;

public class Relic()
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";



	public Bitmap? Icon { get; } = File.Exists(iconPath) ? GameData.GetOrCreateBitmap(iconPath) : null;
	public string Era { get; set; } = "";
	public string Quality { get; set; } = "";
	public ObservableCollection<Reward> Rewards { get; set; } = [];
	public int OwnedCount { get; set; } = 0;
	public bool IsCountVisible => OwnedCount > 0;
	public bool Unowned => OwnedCount == 0;

	public string Type => Name.Split(' ')[0];
}

public class Reward(string name, string type, string iconPath, int count, string rarity, string borderColor)
{
	public string Name { get; set; } = name;
	public string Type { get; set; } = type;
	public Bitmap? Icon { get; } = File.Exists(iconPath) ? GameData.GetOrCreateBitmap(iconPath) : null;
	public int Count { get; set; } = count;
	public string Rarity { get; set; } = rarity;
	public string BorderColor { get; set; } = borderColor;
}
