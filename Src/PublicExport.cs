using Lzma;
using framenion.Src.Riven;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

/// <summary>
/// Downloads and parses Digital Extremes' official PublicExport feed.
/// </summary>
internal static class PublicExport
{
	private const string indexUrl = "https://origin.warframe.com/PublicExport/index_en.txt.lzma";
	private const string manifestBaseUrl = "https://content.warframe.com/PublicExport/Manifest/";

	private const string manifestFile = "ExportManifest.json";
	private const string regionsFile = "ExportRegions_en.json";
	private const string warframesFile = "ExportWarframes_en.json";
	private const string weaponsFile = "ExportWeapons_en.json";
	private const string sentinelsFile = "ExportSentinels_en.json";
	private const string recipesFile = "ExportRecipes_en.json";
	private const string resourcesFile = "ExportResources_en.json";
	private const string relicsFile = "ExportRelicArcane_en.json";
	private const string upgradesFile = "ExportUpgrades_en.json";

	// The feed ships 16 files; only these are ever read. Skipping the rest saves ~2 MB of
	// download and disk per update.
	private static readonly string[] requiredFiles = [
		manifestFile, regionsFile, warframesFile, weaponsFile, sentinelsFile, recipesFile, resourcesFile, relicsFile, upgradesFile,
	];

	private const int maxParallelDownloads = 4;

	private static string IndexPath => Path.Combine(AppData.CacheDir, "manifest");

	private static string CachePath(string fileName) => Path.Combine(AppData.CacheDir, fileName);

	/// <summary>Brings the local cache in line with the remote index. Returns true if anything was downloaded.</summary>
	public static async Task<bool> SyncAsync()
	{
		Directory.CreateDirectory(AppData.CacheDir);

		var remoteHashes = await FetchIndexAsync();
		if (remoteHashes.Count == 0) {
			Log.Warn("PublicExport index was empty; keeping the existing cache.");
			return false;
		}

		var localHashes = await ReadLocalIndexAsync();
		var stale = new List<(string File, string Hash)>();
		foreach (var fileName in requiredFiles) {
			if (!remoteHashes.TryGetValue(fileName, out var remoteHash)) {
				Log.Warn($"PublicExport index does not list {fileName}.");
				continue;
			}

			bool current = localHashes.TryGetValue(fileName, out var localHash)
				&& string.Equals(localHash, remoteHash, StringComparison.Ordinal)
				&& File.Exists(CachePath(fileName));
			if (!current) stale.Add((fileName, remoteHash));
		}

		if (stale.Count == 0) return false;

		Log.Info($"Updating {stale.Count} export file(s): {string.Join(", ", stale.ConvertAll(s => s.File))}");
		Notify.Toast("Data initialization", "A new update has been found, updating cache");

		var downloaded = new List<(string File, string Hash)>();
		var gate = new SemaphoreSlim(maxParallelDownloads);
		var sync = new object();

		await Parallel.ForEachAsync(stale, async (entry, _) => {
			await gate.WaitAsync();
			try {
				// The CDN only serves the hash-suffixed name; requesting the bare file name is a 404.
				await DownloadAtomicAsync($"{manifestBaseUrl}{entry.File}!{entry.Hash}", CachePath(entry.File));
				lock (sync) {
					downloaded.Add(entry);
				}
			} catch (Exception ex) {
				Log.Error($"Failed to download {entry.File}.", ex);
			} finally {
				gate.Release();
			}
		});

		// Record only what actually landed, so a failed download is retried on the next launch
		// instead of being remembered as current.
		foreach (var entry in downloaded) {
			localHashes[entry.File] = entry.Hash;
		}
		await WriteLocalIndexAsync(localHashes);

		if (downloaded.Count != stale.Count) {
			throw new IOException($"Only {downloaded.Count} of {stale.Count} export files could be downloaded. See {Log.Path}.");
		}
		return true;
	}

	/// <summary>Parses the cached export files into <see cref="GameData"/>.</summary>
	public static async Task LoadAsync()
	{
		// The manifest resolves icon paths for everything else, so it has to come first.
		GameData.ExportManifest = await ParseManifestAsync();
		GameData.ExportRegions = await ParseAsync(regionsFile, ExportJsonContext.Default.ExportRegionsRoot, r => r.Items, r => r.UniqueName);
		GameData.ExportRecipes = await ParseAsync(recipesFile, ExportJsonContext.Default.ExportRecipesRoot, r => r.Items, r => r.ResultType);
		GameData.RecipesByUniqueName = await ParseAsync(recipesFile, ExportJsonContext.Default.ExportRecipesRoot, r => r.Items, r => r.UniqueName);
		GameData.ExportResources = await ParseAsync(resourcesFile, ExportJsonContext.Default.ExportResourcesRoot, r => r.Items, r => r.UniqueName, ApplyIcon);
		GameData.ExportWarframes = await ParseAsync(warframesFile, ExportJsonContext.Default.ExportWarframesRoot, r => r.Items, r => r.UniqueName, ApplyIcon);
		GameData.ExportWeapons = await ParseAsync(weaponsFile, ExportJsonContext.Default.ExportWeaponsRoot, r => r.Items, r => r.UniqueName, ApplyIcon);
		GameData.ExportSentinels = await ParseAsync(sentinelsFile, ExportJsonContext.Default.ExportSentinelsRoot, r => r.Items, r => r.UniqueName, ApplyIcon);
		GameData.ExportRelics = await ParseRelicsAsync();
		GameData.RivenMods = await ParseRivenModsAsync();
	}

	/// <summary>
	/// Riven stat pools. Only the "Randomized" upgrades that carry entries are riven mod types;
	/// the veiled variants list none.
	/// </summary>
	private static async Task<FrozenDictionary<string, RivenModType>> ParseRivenModsAsync()
	{
		var path = CachePath(upgradesFile);
		if (!File.Exists(path)) {
			Log.Warn("ExportUpgrades missing from cache; riven grading will be unavailable.");
			return FrozenDictionary<string, RivenModType>.Empty;
		}

		await using var stream = File.OpenRead(path);
		var root = await JsonSerializer.DeserializeAsync(stream, ExportJsonContext.Default.ExportUpgradesRoot);
		if (root == null) return FrozenDictionary<string, RivenModType>.Empty;

		var builder = new Dictionary<string, RivenModType>(StringComparer.Ordinal);
		foreach (var upgrade in root.Items) {
			if (upgrade.UpgradeEntries.Length == 0) continue;
			if (!upgrade.UniqueName.Contains("Randomized", StringComparison.Ordinal)) continue;

			var entries = new List<RivenUpgradeEntry>(upgrade.UpgradeEntries.Length);
			foreach (var entry in upgrade.UpgradeEntries) {
				if (string.IsNullOrEmpty(entry.Tag) || entry.UpgradeValues.Length == 0) continue;
				var value = entry.UpgradeValues[0];
				entries.Add(new RivenUpgradeEntry(entry.Tag, value.Value, RivenStatTables.CleanLocTag(value.LocTag), entry.PrefixTag, entry.SuffixTag));
			}

			if (entries.Count == 0) continue;
			builder[upgrade.UniqueName] = new RivenModType(upgrade.UniqueName, upgrade.Name, entries);
		}

		Log.Info($"Riven mod types indexed: {builder.Count}");
		return builder.ToFrozenDictionary(StringComparer.Ordinal);
	}

	private static async Task<FrozenDictionary<string, string>> ParseManifestAsync()
	{
		var path = CachePath(manifestFile);
		if (!File.Exists(path)) return FrozenDictionary<string, string>.Empty;

		await using var stream = File.OpenRead(path);
		var root = await JsonSerializer.DeserializeAsync(stream, ExportJsonContext.Default.ExportManifestRoot);
		if (root == null) return FrozenDictionary<string, string>.Empty;

		var builder = new Dictionary<string, string>(root.Items.Length, StringComparer.Ordinal);
		foreach (var entry in root.Items) {
			if (string.IsNullOrEmpty(entry.UniqueName) || string.IsNullOrEmpty(entry.TextureLocation)) continue;
			builder[entry.UniqueName] = entry.TextureLocation.Replace('\\', '/').TrimStart('/');
		}
		return builder.ToFrozenDictionary(StringComparer.Ordinal);
	}

	private static async Task<FrozenDictionary<string, RelicDTO>> ParseRelicsAsync()
	{
		var path = CachePath(relicsFile);
		if (!File.Exists(path)) return FrozenDictionary<string, RelicDTO>.Empty;

		await using var stream = File.OpenRead(path);
		var root = await JsonSerializer.DeserializeAsync(stream, ExportJsonContext.Default.ExportRelicArcaneRoot);
		if (root == null) return FrozenDictionary<string, RelicDTO>.Empty;

		var builder = new Dictionary<string, RelicDTO>(StringComparer.Ordinal);
		foreach (var relic in root.Items) {
			// The file also carries arcanes; only void projections are relics.
			if (string.IsNullOrEmpty(relic.UniqueName) || !relic.UniqueName.Contains("/Projections/", StringComparison.Ordinal)) continue;
			ApplyIcon(relic.UniqueName, relic);
			builder[relic.UniqueName] = relic;
		}
		return builder.ToFrozenDictionary(StringComparer.Ordinal);
	}

	private static async Task<FrozenDictionary<string, TItem>> ParseAsync<TRoot, TItem>(
		string fileName,
		System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRoot> typeInfo,
		Func<TRoot, TItem[]> selectItems,
		Func<TItem, string> selectKey,
		Action<string, TItem>? configure = null) where TRoot : class
	{
		var path = CachePath(fileName);
		if (!File.Exists(path)) {
			Log.Warn($"Export file missing from cache: {fileName}");
			return FrozenDictionary<string, TItem>.Empty;
		}

		await using var stream = File.OpenRead(path);
		var root = await JsonSerializer.DeserializeAsync(stream, typeInfo);
		if (root == null) return FrozenDictionary<string, TItem>.Empty;

		var items = selectItems(root);
		var builder = new Dictionary<string, TItem>(items.Length, StringComparer.Ordinal);
		foreach (var item in items) {
			var key = selectKey(item);
			if (string.IsNullOrEmpty(key)) continue;
			configure?.Invoke(key, item);
			builder[key] = item;
		}
		return builder.ToFrozenDictionary(StringComparer.Ordinal);
	}

	/// <summary>The manifest's texture location is authoritative; the per-item icon is the fallback.</summary>
	private static void ApplyIcon<T>(string uniqueName, T item)
	{
		if (!GameData.ExportManifest.TryGetValue(uniqueName, out var texture) || string.IsNullOrEmpty(texture)) return;

		switch (item) {
			case ItemDTO dto: dto.Icon = texture; break;
			case ResourceDTO dto: dto.Icon = texture; break;
			case RelicDTO dto: dto.Icon = texture; break;
		}
	}

	private static async Task<Dictionary<string, string>> FetchIndexAsync()
	{
		var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
		try {
			using var indexStream = await AppData.GetStreamAsync(indexUrl);
			string text;
			using (var lzmaStream = new LzmaStream(indexStream, CompressionMode.Decompress, false))
			using (var reader = new StreamReader(lzmaStream)) {
				text = await reader.ReadToEndAsync();
			}

			foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
				var parts = line.Split('!', 2);
				if (parts.Length == 2) hashes[parts[0]] = parts[1];
			}
		} catch (Exception ex) {
			// Offline start is fine as long as the cache is populated.
			Log.Warn("Could not fetch the PublicExport index.", ex);
		}
		return hashes;
	}

	private static async Task<Dictionary<string, string>> ReadLocalIndexAsync()
	{
		var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
		if (!File.Exists(IndexPath)) return hashes;

		try {
			foreach (var line in await File.ReadAllLinesAsync(IndexPath)) {
				var parts = line.Split('!', 2);
				if (parts.Length == 2) hashes[parts[0]] = parts[1];
			}
		} catch (Exception ex) {
			Log.Warn("Could not read the cached PublicExport index.", ex);
		}
		return hashes;
	}

	private static async Task WriteLocalIndexAsync(Dictionary<string, string> hashes)
	{
		try {
			var lines = new List<string>(hashes.Count);
			foreach (var (file, hash) in hashes) {
				lines.Add($"{file}!{hash}");
			}
			lines.Sort(StringComparer.Ordinal);
			await File.WriteAllLinesAsync(IndexPath, lines);
		} catch (Exception ex) {
			Log.Error("Could not write the cached PublicExport index.", ex);
		}
	}

	/// <summary>Downloads via a temp file so an interrupted transfer cannot leave a truncated cache entry.</summary>
	private static async Task DownloadAtomicAsync(string url, string destination)
	{
		var temp = destination + ".part";
		try {
			using (var source = await AppData.GetStreamAsync(url))
			await using (var file = File.Create(temp)) {
				await source.CopyToAsync(file);
			}

			File.Move(temp, destination, overwrite: true);
		} finally {
			if (File.Exists(temp)) {
				try { File.Delete(temp); } catch { /* best effort */ }
			}
		}
	}
}
