using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace framenion.Src;

public sealed class FissureAlertEntry
{
	public event PropertyChangedEventHandler? PropertyChanged;

	private string type = string.Empty;
	private string relicTier = string.Empty;
	private string planet = string.Empty;
	private string mode = string.Empty;

	[JsonPropertyName("type")]
	public string Type
	{
		get => type;
		set => SetField(ref type, value);
	}

	[JsonPropertyName("relic_tier")]
	public string RelicTier
	{
		get => relicTier;
		set => SetField(ref relicTier, value);
	}

	[JsonPropertyName("planet")]
	public string Planet
	{
		get => planet;
		set => SetField(ref planet, value);
	}

	[JsonPropertyName("mode")]
	public string Mode
	{
		get => mode;
		set => SetField(ref mode, value);
	}

	private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, value)) return;

		field = value;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}

public static class FissureAlertList
{
	private static readonly string DataFilePath = Path.Combine(GameData.appDataDir, "fissures_filter.json");

	public static IReadOnlyList<FissureAlertEntry> Load()
	{
		try {
			if (!File.Exists(DataFilePath)) return [];
			using var stream = File.OpenRead(DataFilePath);
			using var doc = JsonDocument.Parse(stream);
			if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];

			var list = new List<FissureAlertEntry>();
			foreach (var el in doc.RootElement.EnumerateArray()) {
				list.Add(new FissureAlertEntry {
					Type = ReadString(el, "type", "Any"),
					RelicTier = ReadString(el, "relic_tier", "Any"),
					Planet = ReadString(el, "planet", "Any"),
					Mode = ReadString(el, "mode", "Any"),
				});
			}
			return list;
		} catch {
			return [];
		}
	}

	public static bool MatchesAny(VoidFissure fissure, IReadOnlyList<FissureAlertEntry> filters)
	{
		if (filters.Count == 0) return false;

		foreach (var f in filters) {
			if (!Matches(fissure, f)) continue;
			return true;
		}
		return false;
	}

	private static bool Matches(VoidFissure fissure, FissureAlertEntry f)
	{
		static bool IsAny(string? s) => string.IsNullOrWhiteSpace(s) || s.Equals("Any", StringComparison.OrdinalIgnoreCase);

		var mode = fissure.IsHard ? "Steel Path" : "Normal";

		if (!IsAny(f.Type) && !string.Equals(fissure.MissionType, f.Type, StringComparison.OrdinalIgnoreCase)) return false;
		if (!IsAny(f.RelicTier) && !string.Equals(fissure.Tier, f.RelicTier, StringComparison.OrdinalIgnoreCase)) return false;
		if (!IsAny(f.Planet) && !string.Equals(fissure.Planet, f.Planet, StringComparison.OrdinalIgnoreCase)) return false;
		if (!IsAny(f.Mode) && !string.Equals(mode, f.Mode, StringComparison.OrdinalIgnoreCase)) return false;

		return true;
	}

	private static string ReadString(JsonElement element, string property, string fallback)
	=> element.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? fallback) : fallback;
}