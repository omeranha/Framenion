using Avalonia;
using Avalonia.Media;
using HarfBuzzSharp;
using MicroCom.Runtime;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
namespace framenion.Src;

public class VoidFissure : INotifyPropertyChanged
{
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

	private bool _shouldNotify;
	public bool ShouldNotify
	{
		get => _shouldNotify;
		set {
			if (_shouldNotify == value) return;
			_shouldNotify = value;
			OnPropertyChanged(nameof(ShouldNotify));
			OnPropertyChanged(nameof(RowBackground));
			OnPropertyChanged(nameof(RowBorderBrush));
			OnPropertyChanged(nameof(RowBorderThickness));
		}
	}

	public IBrush RowBackground => ShouldNotify ? Brush.Parse("#332B00") : Brush.Parse("#252525");

	public IBrush RowBorderBrush => ShouldNotify ? Brush.Parse("#FFD700") : Brush.Parse("#4A4A4A");

	public Thickness RowBorderThickness => ShouldNotify ? new Thickness(2) : new Thickness(1);

	public event PropertyChangedEventHandler? PropertyChanged;
	public void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	public void UpdateTimeRemaining() => OnPropertyChanged("TimeRemaining");

	public static async Task LoadVoidFissures()
	{
		try {
			using var stream = await AppData.GetStreamAsync("https://api.warframe.com/cdn/worldState.php");
			using var worldState = await JsonDocument.ParseAsync(stream);
			if (!worldState.RootElement.TryGetProperty("ActiveMissions", out var activeMissions) || activeMissions.ValueKind != JsonValueKind.Array) return;

			GameData.Fissures.Clear();
			var culture = new CultureInfo("en-US", false).TextInfo;
			foreach (var mission in activeMissions.EnumerateArray()) {
				var modifier = mission.GetProperty("Modifier").ToString();
				var timestamp = long.Parse(mission.GetProperty("Expiry").GetProperty("$date").GetProperty("$numberLong").ToString());
				var node = mission.GetProperty("Node").ToString();
				var nodeInfo = GameData.ExportRegions[node];
				int baseLvl = (mission.TryGetProperty("Hard", out var hardEl) && hardEl.GetBoolean()) ? 100 : 0;
				var relicInfo = GameData.relicType.TryGetValue(modifier, out (string name, string color) relic);
				var fissure = new VoidFissure {
					Id = mission.GetProperty("_id").GetProperty("$oid").ToString(),
					Modifier = modifier,
					Node = nodeInfo.Name,
					IsHard = baseLvl == 100,
					Tier = relic.name,
					Color = relic.color,
					Expiry = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime,
					Planet = nodeInfo.SystemName,
					Faction = GetFaction(nodeInfo.FactionIndex),
					MissionType = GetMissionType(nodeInfo.MissionIndex),
					MinLevel = nodeInfo.MinEnemyLevel + baseLvl + 5,
					MaxLevel = nodeInfo.MaxEnemyLevel + baseLvl + 5
				};

				GameData.Fissures.Add(fissure);
			}
		} catch (Exception ex) {
			MessageBox.Show("Error", "Could not load Void Fissures: " + ex.Message);
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