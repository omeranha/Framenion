using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace framenion.Src;

[JsonSerializable(typeof(ItemDTO))]
[JsonSerializable(typeof(ResourceDTO))]
[JsonSerializable(typeof(RecipeDTO))]
[JsonSerializable(typeof(RecipeIngredientDTO))]
[JsonSerializable(typeof(RegionDTO))]

[JsonSerializable(typeof(Dictionary<string, ItemDTO>))]
[JsonSerializable(typeof(Dictionary<string, ResourceDTO>))]
[JsonSerializable(typeof(Dictionary<string, RecipeDTO>))]
[JsonSerializable(typeof(Dictionary<string, RegionDTO>))]

public partial class ExportJsonContext : JsonSerializerContext { }
