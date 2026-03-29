using Avalonia.Controls;
using framenion.Src;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace framenion;

public partial class FissuresAlert : Window
{
	private readonly string dataFilePath = Path.Combine(GameData.appDataDir, "fissures_filter.json");
	public ObservableCollection<FissureAlertEntry> Filters {  get; set; } = [];
	public List<string> MissionTypes { get; } = ["Any"];
	public List<string> Planets { get; } = ["Any"];
	public List<string> FissureTypes { get; } = ["Any"];
	public List<string> Modes { get; } = ["Any", "Normal", "Steel Path"];
	public FissuresAlert()
	{
		InitializeComponent();
		this.DataContext = this;
		this.WindowStartupLocation = WindowStartupLocation.CenterOwner;
		var culture = new CultureInfo("en-US", false).TextInfo;
		var missionNames = GameData.exportMissionTypes.Values.Select(el => {
			try {
				string nameKey = el.TryGetProperty("name", out var nameProp) ? nameProp.ToString() : string.Empty;
				return GameData.lang.TryGetValue(nameKey, out var v) ? culture.ToTitleCase(v.ToLower()) : nameKey;
			} catch { return string.Empty; }
		}).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
		var planetNames = GameData.exportRegions.Values.Select(el => {
			try {
				var systemName = el.TryGetProperty("systemName", out var sysProp) ? sysProp.ToString() : string.Empty;
				return GameData.lang.TryGetValue(systemName, out var v) ? v : systemName;
			} catch { return string.Empty; }
		}).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).OrderBy(s => s).ToList();

		MissionTypes.AddRange(missionNames);
		Planets.AddRange(planetNames);
		FissureTypes.AddRange([.. GameData.relicType.Values.Select(v => v.Item1).Distinct()]);

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
			Console.WriteLine("Failed to load fissure filters: " + ex.Message);
		}
	}

	private void Save_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		try {
			using var fs = File.Create(dataFilePath);
			var options = new JsonWriterOptions { Indented = true };
			using var writer = new Utf8JsonWriter(fs, options);
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
			writer.Flush();
			ToastWindow.ShowToast(this, "Fissure Alert List", "Saved", TimeSpan.FromSeconds(3), ToastAnchor.TopRightOfOwnerWindow);
		} catch (Exception ex) {
			MessageBox.Show(this, "Error", "Failed to save filters: " + ex.Message);
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
