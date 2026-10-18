using Avalonia;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion.Src;

public class VoidFissure : INotifyPropertyChanged
{
	private static readonly IBrush notifyBackground = new SolidColorBrush(Avalonia.Media.Color.Parse("#332B00"));
	private static readonly IBrush normalBackground = new SolidColorBrush(Avalonia.Media.Color.Parse("#252525"));
	private static readonly IBrush notifyBorder = new SolidColorBrush(Avalonia.Media.Color.Parse("#FFD700"));
	private static readonly IBrush normalBorder = new SolidColorBrush(Avalonia.Media.Color.Parse("#4A4A4A"));
	private static readonly Thickness notifyThickness = new(2);
	private static readonly Thickness normalThickness = new(1);

	public string Id { get; set; } = string.Empty;
	public string Tier { get; set; } = string.Empty;
	public string MissionType { get; set; } = string.Empty;
	public string Modifier { get; set; } = string.Empty;
	public string Node { get; set; } = string.Empty;
	public string Planet { get; set; } = string.Empty;
	public string Faction { get; set; } = string.Empty;
	public int MinLevel { get; set; }
	public int MaxLevel { get; set; }
	public DateTime Expiry { get; set; }
	public bool IsHard { get; set; }
	public string Color { get; set; } = string.Empty;
	public string LevelRange => $"({MinLevel}-{MaxLevel})";
	public string Location => $"{Node} ({Planet})";

	public string TimeRemaining
	{
		get {
			var remaining = Expiry - DateTime.UtcNow;
			if (remaining.TotalSeconds <= 0) return "Expired";
			if (remaining.TotalHours >= 1)
				return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
			if (remaining.TotalMinutes >= 1)
				return $"{remaining.Minutes}m {remaining.Seconds}s";
			return $"{remaining.Seconds}s";
		}
	}

	private bool shouldNotify;
	public bool ShouldNotify
	{
		get => shouldNotify;
		set {
			if (shouldNotify == value) return;
			shouldNotify = value;
			OnPropertyChanged(nameof(ShouldNotify));
			OnPropertyChanged(nameof(RowBackground));
			OnPropertyChanged(nameof(RowBorderBrush));
			OnPropertyChanged(nameof(RowBorderThickness));
		}
	}

	// Cached brushes: these getters are read for every realized row, and Brush.Parse allocates.
	public IBrush RowBackground => ShouldNotify ? notifyBackground : normalBackground;

	public IBrush RowBorderBrush => ShouldNotify ? notifyBorder : normalBorder;

	public Thickness RowBorderThickness => ShouldNotify ? notifyThickness : normalThickness;

	public event PropertyChangedEventHandler? PropertyChanged;
	public void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	public void UpdateTimeRemaining() => OnPropertyChanged(nameof(TimeRemaining));

	/// <summary>
	/// Refreshes the active fissure list from the world state. Builds into a local list and only
	/// swaps it in on success, so a single unrecognised node cannot empty the panel.
	/// </summary>
	public static async Task LoadVoidFissures()
	{
		try {
			using var stream = await AppData.GetStreamAsync("https://api.warframe.com/cdn/worldState.php");
			using var worldState = await JsonDocument.ParseAsync(stream);
			if (!worldState.RootElement.TryGetProperty("ActiveMissions", out var activeMissions) || activeMissions.ValueKind != JsonValueKind.Array) {
				Log.Warn("World state contained no ActiveMissions array.");
				return;
			}

			var fissures = new List<VoidFissure>();
			int skipped = 0;
			foreach (var mission in activeMissions.EnumerateArray()) {
				var fissure = TryReadFissure(mission);
				if (fissure == null) {
					skipped++;
					continue;
				}
				fissures.Add(fissure);
			}

			if (skipped > 0) Log.Debug($"Skipped {skipped} active mission(s) that could not be read as fissures.");
			GameData.Fissures = fissures;
		} catch (Exception ex) {
			Log.Error("Could not load Void Fissures.", ex);
		}
	}

	private static VoidFissure? TryReadFissure(JsonElement mission)
	{
		try {
			if (!mission.TryGetProperty("Modifier", out var modifierEl)) return null;
			var modifier = modifierEl.GetString();
			if (string.IsNullOrEmpty(modifier) || !GameData.relicType.TryGetValue(modifier, out var relic)) return null;

			if (!mission.TryGetProperty("Node", out var nodeEl)) return null;
			var node = nodeEl.GetString();
			if (string.IsNullOrEmpty(node) || !GameData.ExportRegions.TryGetValue(node, out var nodeInfo)) {
				Log.Debug($"Unknown fissure node '{node}'.");
				return null;
			}

			if (!mission.TryGetProperty("Expiry", out var expiryEl)
				|| !expiryEl.TryGetProperty("$date", out var dateEl)
				|| !dateEl.TryGetProperty("$numberLong", out var longEl)
				|| !long.TryParse(longEl.GetString(), out var timestamp)) return null;

			if (!mission.TryGetProperty("_id", out var idEl) || !idEl.TryGetProperty("$oid", out var oidEl)) return null;
			var id = oidEl.GetString();
			if (string.IsNullOrEmpty(id)) return null;

			bool isHard = mission.TryGetProperty("Hard", out var hardEl) && hardEl.ValueKind == JsonValueKind.True;
			int baseLvl = isHard ? 100 : 0;

			return new VoidFissure {
				Id = id,
				Modifier = modifier,
				Node = nodeInfo.Name,
				IsHard = isHard,
				Tier = relic.name,
				Color = relic.color,
				Expiry = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime,
				Planet = nodeInfo.SystemName,
				Faction = GetFaction(nodeInfo.FactionIndex),
				MissionType = GetMissionType(nodeInfo.MissionIndex),
				MinLevel = nodeInfo.MinEnemyLevel + baseLvl + 5,
				MaxLevel = nodeInfo.MaxEnemyLevel + baseLvl + 5
			};
		} catch (Exception ex) {
			Log.Debug($"Malformed active mission entry: {ex.Message}");
			return null;
		}
	}

	public static int GetTierSortKey(string voidTier)
	{
		return voidTier switch {
			"Lith" => 1,
			"Meso" => 2,
			"Neo" => 3,
			"Axi" => 4,
			"Requiem" => 5,
			"Omnia" => 6,
			_ => int.MaxValue
		};
	}

	public static string GetFaction(int index)
	{
		return index switch {
			0 => "Grineer",
			1 => "Corpus",
			2 => "Infested",
			3 => "Corrupted",
			7 => "The Murmur",
			8 => "Scaldra",
			9 => "Techrot",
			_ => "Unknown"
		};
	}

	public static string GetMissionType(int index)
	{
		return index switch {
			0 => "Assassination",
			1 => "Exterminate",
			2 => "Survival",
			3 => "Rescue",
			4 => "Sabotage",
			5 => "Capture",
			7 => "Spy",
			8 => "Defense",
			9 => "Mobile Defense",
			13 => "Interception",
			14 => "Hijack",
			15 => "Hive Sabotage",
			17 => "Excavation",
			21 => "Infested Salvage",
			22 => "Rathuum",
			24 => "Pursuit",
			25 => "Rush",
			26 => "Assault",
			27 => "Defection",
			28 => "Landscape",
			31 => "The Circuit",
			33 => "Disruption",
			34 => "Void Flood",
			35 => "Void Cascade",
			36 => "Void Armageddon",
			38 => "Alchemy",
			40 => "Legacyte Harvest",
			41 => "Shrine Defense",
			42 => "Faceoff",
			_ => "Unknown"
		};
	}
}
