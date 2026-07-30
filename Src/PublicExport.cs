using Lzma;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion.Src
{
	internal class PublicExport
	{
		public static async Task UpdateManifest()
		{
			using var indexStream = await AppData.GetStreamAsync("https://origin.warframe.com/PublicExport/index_en.txt.lzma");
			string[] remoteIndex;
			using (var lzmaStream = new LzmaStream(indexStream, CompressionMode.Decompress, false))
			using (var reader = new StreamReader(lzmaStream)) {
				var text = await reader.ReadToEndAsync();
				remoteIndex = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
			}

			string localPath = Path.Combine(AppData.CacheDir, "manifest");
			string[] localIndex = File.Exists(localPath) ? File.ReadAllLines(localPath) : [];
			var localHashes = localIndex.Select(x => x.Split('!', 2)).Where(x => x.Length == 2).ToDictionary(x => x[0], x => x[1]);
			var outdatedFiles = new List<string>();
			foreach (var line in remoteIndex) {
				var parts = line.Split('!', 2);
				if (parts.Length != 2) continue;

				string fileName = parts[0];
				string remoteHash = parts[1];
				if (!localHashes.TryGetValue(fileName, out var localHash)) {
					outdatedFiles.Add($"{fileName}!{remoteHash}");
					continue;
				}

				if (!string.Equals(localHash, remoteHash, StringComparison.Ordinal)) {
					outdatedFiles.Add(fileName);
				}
			}

			if (outdatedFiles.Count > 0) {
				ToastWindow.Show("Data initialization", "A new update has been found, updating cache");
				Directory.CreateDirectory("cache");
				foreach (var file in outdatedFiles) {
					await AppData.DownloadToFileAsync($"http://content.warframe.com/PublicExport/Manifest/{file}", Path.Combine(AppData.CacheDir, file.Split('!')[0]));
				}
			}
			File.WriteAllLines(localPath, remoteIndex);
		}

		public static async Task ParseExportManifest()
		{
			var manifest = Path.Combine(AppData.CacheDir, "ExportManifest.json");
			if (!File.Exists(manifest)) return;

			await using var stream = File.OpenRead(manifest);
			using var doc = await JsonDocument.ParseAsync(stream);
			var builder = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (var element in doc.RootElement.EnumerateObject().First().Value.EnumerateArray()) {
				element.TryGetProperty("uniqueName", out var uniqueName);
				element.TryGetProperty("textureLocation", out var textureLocation);
				builder[uniqueName.ToString()] = textureLocation.ToString();
			}
			GameData.ExportManifest = builder.ToFrozenDictionary();
		}

		public static async Task ParseRegions()
		{
			var regions = Path.Combine(AppData.CacheDir, "ExportRegions_en.json");
			if (!File.Exists(regions)) return;

			await using var stream = File.OpenRead(regions);
			using var doc = await JsonDocument.ParseAsync(stream);
			var builder = new Dictionary<string, RegionDTO>(StringComparer.Ordinal);
			foreach (var element in doc.RootElement.EnumerateObject().First().Value.EnumerateArray()) {
				var region = JsonSerializer.Deserialize<RegionDTO>(element, ExportJsonContext.Default.RegionDTO);
				if (region != null) {
					builder[region.UniqueName] = region;
				}
			}
			GameData.ExportRegions = builder.ToFrozenDictionary();
		}

		public static async Task ParseFile(string name)
		{
			var cacheFile = Path.Combine(AppData.CacheDir, name);
			switch (name) {
				case "ExportWarframes_en.json":
					GameData.ExportWarframes = await DeserializeItem(cacheFile);
					break;
				case "ExportWeapons_en.json":
					GameData.ExportWeapons = await DeserializeItem(cacheFile);
					break;
				case "ExportRecipes_en.json": {
					await using var stream = File.OpenRead(cacheFile);
					using var doc = await JsonDocument.ParseAsync(stream);
					var builder = new Dictionary<string, RecipeDTO>(StringComparer.Ordinal);
					foreach (var element in doc.RootElement.EnumerateObject().First().Value.EnumerateArray()) {
						var item = JsonSerializer.Deserialize<RecipeDTO>(element, ExportJsonContext.Default.RecipeDTO);
						if (item != null) {
							builder[item.ResultType] = item;
						}
					}
					GameData.ExportRecipes = builder.ToFrozenDictionary(StringComparer.Ordinal);
						break;
					}
				case "ExportResources_en.json": {
						await using var stream = File.OpenRead(cacheFile);
						using var doc = await JsonDocument.ParseAsync(stream);
						var builder = new Dictionary<string, ResourceDTO>(StringComparer.Ordinal);
						foreach (var element in doc.RootElement.EnumerateObject().First().Value.EnumerateArray()) {
							var item = JsonSerializer.Deserialize<ResourceDTO>(element, ExportJsonContext.Default.ResourceDTO);
							if (item != null) {
								GameData.ExportManifest.TryGetValue(item.UniqueName, out var iconPath);
								if (iconPath != null) {
									item.Icon = iconPath;
								}
								builder[item.UniqueName] = item;
							}
						}
						GameData.ExportResources = builder.ToFrozenDictionary(StringComparer.Ordinal);
						break;
					}
				case "ExportRelicArcane_en.json": {
						await using var stream = File.OpenRead(cacheFile);
						using var doc = await JsonDocument.ParseAsync(stream);
						var builder = new Dictionary<string, RelicDTO>(StringComparer.Ordinal);
						foreach (var element in doc.RootElement.EnumerateObject().First().Value.EnumerateArray()) {
							element.TryGetProperty("uniqueName", out var unique);
							if (!unique.GetString()!.Contains("/Projections/")) continue;

							var item = JsonSerializer.Deserialize<RelicDTO>(element, ExportJsonContext.Default.RelicDTO);
							if (item != null) {
								GameData.ExportManifest.TryGetValue(item.UniqueName, out var iconPath);
								if (iconPath != null) {
									item.Icon = iconPath;
								}
								builder[item.UniqueName] = item;
							}
						}
						GameData.ExportRelics = builder.ToFrozenDictionary(StringComparer.Ordinal);
						break;
					}
				default:
					break;
			}
		}

		public static async Task<FrozenDictionary<string, ItemDTO>> DeserializeItem(string path)
		{
			await using var stream = File.OpenRead(path);
			using var doc = await JsonDocument.ParseAsync(stream);
			var builder = new Dictionary<string, ItemDTO>(StringComparer.Ordinal);
			foreach (var element in doc.RootElement.EnumerateObject().First().Value.EnumerateArray()) {
				var item = JsonSerializer.Deserialize<ItemDTO>(element, ExportJsonContext.Default.ItemDTO);
				if (item != null) {
					GameData.ExportManifest.TryGetValue(item.UniqueName, out var iconPath);
					if (iconPath != null) {
						item.Icon = iconPath;
					}
					builder[item.UniqueName] = item;
				}
			}
			return builder.ToFrozenDictionary(StringComparer.Ordinal);
		}
	}
}
