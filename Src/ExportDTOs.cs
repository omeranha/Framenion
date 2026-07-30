using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace framenion.Src;

public class ItemDTO
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("icon")]
	public string Icon { get; set; } = "";

	[JsonPropertyName("productCategory")]
	public string Category { get; set; } = "";

	[JsonPropertyName("partType")]
	public string? PartType { get; set; }

	[JsonPropertyName("maxLevelCap")]
	public int? MaxLevelCap { get; set; }

	[JsonPropertyName("description")]
	public string? Description { get; set; }
}

public class RecipeDTO
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("resultType")]
	public string ResultType { get; set; } = "";

	[JsonPropertyName("ingredients")]
	public List<RecipeIngredientDTO> Ingredients { get; set; } = [];
}

public class RecipeIngredientDTO
{
	[JsonPropertyName("ItemType")]
	public string Type { get; set; } = "";

	[JsonPropertyName("ItemCount")]
	public int Count { get; set; } = 1;

	[JsonPropertyName("ProductCategory")]
	public string ProductCategory { get; set; } = "";
}

public class ResourceDTO
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("icon")]
	public string Icon { get; set; } = "";
}

public class RegionDTO
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("systemName")]
	public string SystemName { get; set; } = "";

	[JsonPropertyName("missionIndex")]
	public int MissionIndex { get; set; } = 0;

	[JsonPropertyName("factionIndex")]
	public int FactionIndex { get; set; } = 0;

	[JsonPropertyName("minEnemyLevel")]
	public int MinEnemyLevel { get; set; } = 0;

	[JsonPropertyName("maxEnemyLevel")]
	public int MaxEnemyLevel { get; set; } = 0;
}

public class RelicDTO
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("icon")]
	public string Icon { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("description")]
	public string Description { get; set; } = "";

	[JsonPropertyName("relicRewards")]
	public RewardDTO[] RelicRewards { get; set; } = [];
}

public class RewardDTO
{
	[JsonPropertyName("rewardName")]
	public string RewardName { get; set; } = "";

	[JsonPropertyName("ItemCount")]
	public int Count { get; set; } = 1;

	[JsonPropertyName("rarity")]
	public string Rarity { get; set; } = "";
}

[JsonSerializable(typeof(ItemDTO))]
[JsonSerializable(typeof(ResourceDTO))]
[JsonSerializable(typeof(RecipeDTO))]
[JsonSerializable(typeof(RecipeIngredientDTO))]
[JsonSerializable(typeof(RegionDTO))]
[JsonSerializable(typeof(RelicDTO))]
[JsonSerializable(typeof(RewardDTO))]

[JsonSerializable(typeof(Dictionary<string, ItemDTO>))]
[JsonSerializable(typeof(Dictionary<string, ResourceDTO>))]
[JsonSerializable(typeof(Dictionary<string, RecipeDTO>))]
[JsonSerializable(typeof(Dictionary<string, RegionDTO>))]
[JsonSerializable(typeof(Dictionary<string, RelicDTO>))]
[JsonSerializable(typeof(Dictionary<string, List<RelicDTO>>))]

public partial class ExportJsonContext : JsonSerializerContext { }
