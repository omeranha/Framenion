using Avalonia;
using Avalonia.Media;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion.Src;

public sealed class AppSettings
{
	public string AppBackground { get; set; } = "#1A1A1A";
	public string PanelBackground { get; set; } = "#2C2C2C";
	public string ButtonBackground { get; set; } = "#2C2C2C";
	public string ButtonForeground { get; set; } = "#FFFFFF";
	public string ButtonSelected { get; set; } = "#4A9EFF";
	public bool EnableNotifications { get; set; } = true;
	public bool EnableEELogRead { get; set;  } = true;
	public bool EnableRelicOverlay { get; set; } = true;
	public int UIScale { get; set; } = 100;
	public bool DebugOCR { get; set; } = false;
	public int OverlayOffset { get; set; } = 200;

	public static string SettingsPath => Path.Combine(GameData.appDataDir, "settings.json");

	public static async Task<AppSettings> LoadAsync()
	{
		var settings = new AppSettings();
		try {
			Directory.CreateDirectory(GameData.appDataDir);
			if (!File.Exists(SettingsPath)) return settings;

			await using var stream = File.OpenRead(SettingsPath);
			using var doc = await JsonDocument.ParseAsync(stream);

			if (doc.RootElement.ValueKind != JsonValueKind.Object) return settings;

			var root = doc.RootElement;
			settings.AppBackground = ReadString(root, "appBackground", settings.AppBackground);
			settings.PanelBackground = ReadString(root, "panelBackground", settings.PanelBackground);
			settings.ButtonBackground = ReadString(root, "buttonBackground", settings.ButtonBackground);
			settings.ButtonForeground = ReadString(root, "buttonForeground", settings.ButtonForeground);
			settings.ButtonSelected = ReadString(root, "buttonSelected", settings.ButtonSelected);
			settings.EnableNotifications = ReadBool(root, "enableNotifications", settings.EnableNotifications);
			settings.EnableEELogRead = ReadBool(root, "enableEELogRead", settings.EnableEELogRead);
			settings.EnableRelicOverlay = ReadBool(root, "enableRelicOverlay", settings.EnableRelicOverlay);
			settings.UIScale = ReadInt(root, "uiScale", settings.UIScale);
			settings.DebugOCR = ReadBool(root, "debugOCR", settings.DebugOCR);
			settings.OverlayOffset = ReadInt(root, "overlayOffset" , settings.OverlayOffset);
			return settings;
		} catch {
			return settings;
		}
	}

	public async Task SaveAsync()
	{
		Directory.CreateDirectory(GameData.appDataDir);

		await using var fs = File.Create(SettingsPath);
		var options = new JsonWriterOptions { Indented = true };
		await using var writer = new Utf8JsonWriter(fs, options);

		writer.WriteStartObject();
		writer.WriteString("appBackground", AppBackground ?? string.Empty);
		writer.WriteString("panelBackground", PanelBackground ?? string.Empty);
		writer.WriteString("buttonBackground", ButtonBackground ?? string.Empty);
		writer.WriteString("buttonForeground", ButtonForeground ?? string.Empty);
		writer.WriteString("buttonSelected", ButtonSelected ?? string.Empty);
		writer.WriteBoolean("enableNotifications", EnableNotifications);
		writer.WriteBoolean("enableEELogRead", EnableEELogRead);
		writer.WriteBoolean("enableRelicOverlay", EnableRelicOverlay);
		writer.WriteNumber("uiScale", UIScale);
		writer.WriteBoolean("debugOCR", DebugOCR);
		writer.WriteNumber("overlayOffset", OverlayOffset);
		writer.WriteEndObject();

		await writer.FlushAsync().ConfigureAwait(false);
	}

	public void ApplyToApplicationResources()
	{
		if (Application.Current is null) return;

		SetBrush("AppBackgroundBrush", AppBackground);
		SetBrush("PanelBackgroundBrush", PanelBackground);
		SetBrush("ButtonBackgroundBrush", ButtonBackground);
		SetBrush("ButtonForegroundBrush", ButtonForeground);
		SetBrush("AccentBrush", ButtonSelected);
	}

	private static void SetBrush(string key, string color)
	{
		if (Application.Current is null) return;

		if (!TryParseColor(color, out var c)) return;

		Application.Current.Resources[key] = new SolidColorBrush(c);
	}

	private static bool TryParseColor(string value, out Color color)
	{
		try {
			color = Color.Parse(value);
			return true;
		} catch {
			color = default;
			return false;
		}
	}

	private static string ReadString(JsonElement root, string propertyName, string fallback) => root.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? fallback) : fallback;

	private static bool ReadBool(JsonElement root, string propertyName, bool fallback) => root.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.True || ((!root.TryGetProperty(propertyName, out p) || p.ValueKind != JsonValueKind.False) && fallback);

	private static int ReadInt(JsonElement root, string propertyName, int fallback) => root.TryGetProperty(propertyName, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value) ? value : fallback;
}
