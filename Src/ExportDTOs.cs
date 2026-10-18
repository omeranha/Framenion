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

	[JsonPropertyName("omegaAttenuation")]
	public double? OmegaAttenuation { get; set; }

	[JsonPropertyName("multishot")]
	public double? Multishot { get; set; }

	[JsonPropertyName("maxLevelCap")]
	public int? MaxLevelCap { get; set; }
}

public class RecipeDTO
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("resultType")]
	public string ResultType { get; set; } = "";

	[JsonPropertyName("primeSellingPrice")]
	public int? Ducats { get; set; }

	[JsonPropertyName("ingredients")]
	public RecipeIngredientDTO[] Ingredients { get; set; } = [];
}

public class RecipeIngredientDTO
{
	[JsonPropertyName("ItemType")]
	public string Type { get; set; } = "";

	[JsonPropertyName("ItemCount")]
	public int Count { get; set; } = 1;
}

public class ResourceDTO
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("icon")]
	public string Icon { get; set; } = "";

	[JsonPropertyName("primeSellingPrice")]
	public int? Ducats { get; set; }
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

	[JsonPropertyName("relicRewards")]
	public RewardDTO[] RelicRewards { get; set; } = [];
}

public class RewardDTO
{
	[JsonPropertyName("rewardName")]
	public string RewardName { get; set; } = "";

	[JsonPropertyName("itemCount")]
	public int Count { get; set; } = 1;

	[JsonPropertyName("rarity")]
	public string Rarity { get; set; } = "";
}

public class ManifestEntryDTO
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("textureLocation")]
	public string TextureLocation { get; set; } = "";
}

#region Rivens
public class UpgradeDTO
{
	[JsonPropertyName("uniqueName")]
	public string UniqueName { get; set; } = "";

	[JsonPropertyName("name")]
	public string Name { get; set; } = "";

	[JsonPropertyName("upgradeEntries")]
	public UpgradeEntryDTO[] UpgradeEntries { get; set; } = [];
}

public class UpgradeEntryDTO
{
	[JsonPropertyName("tag")]
	public string Tag { get; set; } = "";

	[JsonPropertyName("prefixTag")]
	public string PrefixTag { get; set; } = "";

	[JsonPropertyName("suffixTag")]
	public string SuffixTag { get; set; } = "";

	[JsonPropertyName("upgradeValues")]
	public UpgradeValueDTO[] UpgradeValues { get; set; } = [];
}

public class UpgradeValueDTO
{
	[JsonPropertyName("value")]
	public double Value { get; set; }

	/// <summary>Localized label template, e.g. "|val|% &lt;DT_FIRE_COLOR&gt;Heat".</summary>
	[JsonPropertyName("locTag")]
	public string LocTag { get; set; } = "";
}

public class ExportUpgradesRoot
{
	[JsonPropertyName("ExportUpgrades")]
	public UpgradeDTO[] Items { get; set; } = [];
}
#endregion

#region Export roots
// DE's official export wraps every file in a single property named after the file. Declaring
// the wrapper lets the whole file be read in one source-generated pass instead of being
// materialized as a JsonDocument and then re-serialized element by element.
public class ExportManifestRoot
{
	// This one file does not follow the ExportX naming the rest of the feed uses.
	[JsonPropertyName("Manifest")]
	public ManifestEntryDTO[] Items { get; set; } = [];
}

public class ExportWarframesRoot
{
	[JsonPropertyName("ExportWarframes")]
	public ItemDTO[] Items { get; set; } = [];
}

public class ExportWeaponsRoot
{
	[JsonPropertyName("ExportWeapons")]
	public ItemDTO[] Items { get; set; } = [];
}

public class ExportSentinelsRoot
{
	[JsonPropertyName("ExportSentinels")]
	public ItemDTO[] Items { get; set; } = [];
}

public class ExportRecipesRoot
{
	[JsonPropertyName("ExportRecipes")]
	public RecipeDTO[] Items { get; set; } = [];
}

public class ExportResourcesRoot
{
	[JsonPropertyName("ExportResources")]
	public ResourceDTO[] Items { get; set; } = [];
}

public class ExportRegionsRoot
{
	[JsonPropertyName("ExportRegions")]
	public RegionDTO[] Items { get; set; } = [];
}

public class ExportRelicArcaneRoot
{
	[JsonPropertyName("ExportRelicArcane")]
	public RelicDTO[] Items { get; set; } = [];
}
#endregion

[JsonSerializable(typeof(ExportManifestRoot))]
[JsonSerializable(typeof(ExportUpgradesRoot))]
[JsonSerializable(typeof(ExportWarframesRoot))]
[JsonSerializable(typeof(ExportWeaponsRoot))]
[JsonSerializable(typeof(ExportSentinelsRoot))]
[JsonSerializable(typeof(ExportRecipesRoot))]
[JsonSerializable(typeof(ExportResourcesRoot))]
[JsonSerializable(typeof(ExportRegionsRoot))]
[JsonSerializable(typeof(ExportRelicArcaneRoot))]
public partial class ExportJsonContext : JsonSerializerContext { }
