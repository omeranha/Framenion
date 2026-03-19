using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
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
				return $"{(int)remaining.TotalHours}h {remaining.Minutes}m {remaining.Seconds}s";
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

	public static async Task LoadVoidFissures(Window window)
	{
		try {
			using var response = await GameData.httpClient.GetAsync("https://api.warframe.com/cdn/worldState.php",HttpCompletionOption.ResponseHeadersRead);
			response.EnsureSuccessStatusCode();
			await using var stream = await response.Content.ReadAsStreamAsync();
			using var worldState = await JsonDocument.ParseAsync(stream);
			if (!worldState.RootElement.TryGetProperty("ActiveMissions", out var activeMissions) || activeMissions.ValueKind != JsonValueKind.Array) return;

			GameData.fissures.Clear();
			var culture = new CultureInfo("en-US", false).TextInfo;
			foreach (var mission in activeMissions.EnumerateArray()) {
				var modifier = mission.GetProperty("Modifier").ToString();
				var timestamp = long.Parse(mission.GetProperty("Expiry").GetProperty("$date").GetProperty("$numberLong").ToString());
				var node = mission.GetProperty("Node").ToString();
				JsonElement nodeInfo = GameData.exportRegions[node];
				int baseLvl = (mission.TryGetProperty("Hard", out var hardEl) && hardEl.GetBoolean()) ? 100 : 0;
				var missionType = culture.ToTitleCase(GameData.lang[GameData.exportMissionTypes[nodeInfo.GetProperty("missionType").ToString()].GetProperty("name").ToString()].ToLower());
				var fissure = new VoidFissure {
					Id = mission.GetProperty("_id").GetProperty("$oid").ToString(),
					Modifier = modifier,
					Node = GameData.lang[nodeInfo.GetProperty("name").ToString()],
					IsHard = baseLvl == 100,
					Tier = GameData.relicType.TryGetValue(modifier, out (string, string) value1) ? value1.Item1 : "Unknown",
					Color = GameData.relicType.TryGetValue(modifier, out (string, string) value) ? value.Item2 : "#FFFFFF",
					Expiry = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime,
					Planet = GameData.lang[nodeInfo.GetProperty("systemName").ToString()],
					Faction = GameData.lang[GameData.exportFactions[nodeInfo.GetProperty("faction").ToString()].GetProperty("name").ToString()],
					MissionType = missionType,
					MinLevel = nodeInfo.GetProperty("minEnemyLevel").GetInt32() + baseLvl + 5,
					MaxLevel = nodeInfo.GetProperty("maxEnemyLevel").GetInt32() + baseLvl + 5
				};

				GameData.fissures.Add(fissure);
			}
		} catch (Exception ex) {
			MessageBox.Show(window, "Error", "Could not load Void Fissures: " + ex.Message);
		}
	}
}