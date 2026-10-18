using Avalonia.Controls;
using Avalonia.Media;
using framenion.Src;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion;

public partial class FissuresAlert : Window
{
	private readonly string dataFilePath = Path.Combine(AppData.AppDataDir, "fissures_filter.json");
	public ObservableCollection<FissureAlertEntry> Filters {  get; set; } = [];
	public List<string> MissionTypes { get; } = ["Any"];
	public List<string> Planets { get; } = ["Any"];
	public List<string> FissureTypes { get; } = ["Any"];
	public List<string> Modes { get; } = ["Any", "Normal", "Steel Path"];
	public FissuresAlert()
	{
		InitializeComponent();
		DataContext = this;
		WindowStartupLocation = WindowStartupLocation.CenterOwner;

		// Mission and planet names come straight from the official region table, which is
		// already localized; the old language-dictionary lookup no longer exists.
		var missionNames = GameData.ExportRegions.Values
			.Select(region => VoidFissure.GetMissionType(region.MissionIndex))
			.Where(name => name != "Unknown")
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

		var planetNames = GameData.ExportRegions.Values
			.Select(region => region.SystemName?.Trim() ?? string.Empty)
			.Where(name => !string.IsNullOrWhiteSpace(name))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

		MissionTypes.AddRange(missionNames);
		Planets.AddRange(planetNames);
		FissureTypes.AddRange(GameData.relicType.Values.Select(v => v.name).Distinct(StringComparer.Ordinal));

		LoadFilters();
	}

	private void LoadFilters()
	{
		Filters.Clear();
		try {
			if (!File.Exists(dataFilePath)) {
				return;
			}
			using var stream = File.OpenRead(dataFilePath);
			using var doc = JsonDocument.Parse(stream);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Array) {
				return;
			}

			foreach (var el in root.EnumerateArray()) {
				string type = el.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() ?? string.Empty : string.Empty;
				string relic = el.TryGetProperty("relic_tier", out var rEl) && rEl.ValueKind == JsonValueKind.String ? rEl.GetString() ?? string.Empty : string.Empty;
				string planet = el.TryGetProperty("planet", out var pEl) && pEl.ValueKind == JsonValueKind.String ? pEl.GetString() ?? string.Empty : string.Empty;
				string mode = el.TryGetProperty("mode", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() ?? string.Empty : string.Empty;
				Filters.Add(new FissureAlertEntry {
					Type = type,
					RelicTier = relic,
					Planet = planet,
					Mode = mode
				});
			}
		} catch (Exception ex) {
			Log.Warn("Failed to load fissure filters.", ex);
		}
	}

	private async void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		var originalBackground = SaveButton.Background;
		try {
			await using (var fs = File.Create(dataFilePath)) {
				await using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
				writer.WriteStartArray();
				foreach (var f in Filters) {
					writer.WriteStartObject();
					writer.WriteString("type", f.Type ?? string.Empty);
					writer.WriteString("relic_tier", f.RelicTier ?? string.Empty);
					writer.WriteString("planet", f.Planet ?? string.Empty);
					writer.WriteString("mode", f.Mode ?? string.Empty);
					writer.WriteEndObject();
				}
				writer.WriteEndArray();
				await writer.FlushAsync();
			}

			SaveButton.Content = "Saved";
			SaveButton.Background = Brushes.LimeGreen;
			await Task.Delay(2000);
			// The window can be closed while the confirmation is showing.
			if (!IsVisible) return;
			SaveButton.Content = "Save";
			SaveButton.Background = originalBackground;
		} catch (Exception ex) {
			Log.Error("Failed to save fissure filters.", ex);
			MessageBox.Show("Error", "Failed to save filters: " + ex.Message);
		}
	}

	private void OnAddEntryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		Filters.Add(new FissureAlertEntry {
			Type = "Any",
			RelicTier = "Any",
			Planet = "Any",
			Mode = "Any"
		});
	}

	private void OnRemoveEntryClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		if (sender is Button btn && btn.DataContext is FissureAlertEntry entry) {
			Filters.Remove(entry);
		}
	}
}
