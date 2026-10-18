using Avalonia;
using Avalonia.Media;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion.Src;

public sealed class AppSettings
{
	private const string defaultAccent = "#4A9EFF";

	public string AccentColor { get; set; } = defaultAccent;
	public bool EnableNotifications { get; set; } = true;
	public bool EnableEELogRead { get; set; } = true;
	public bool EnableRelicOverlay { get; set; } = true;
	public bool EnableRivenOverlay { get; set; } = true;
	public int UIScale { get; set; } = 100;
	public bool DebugOCR { get; set; } = false;
	public int OverlayOffset { get; set; } = 200;

	/// <summary>Optional per-screen overrides; null entries keep the measured defaults.</summary>
	public ScanRegions Regions { get; set; } = new();

	public static string SettingsPath => Path.Combine(AppData.AppDataDir, "settings.json");

	/// <summary>
	/// Synchronous because the settings have to be in place before the main window is built:
	/// the monitor and the OCR overlay both read them during construction.
	/// </summary>
	public static AppSettings Load()
	{
		try {
			Directory.CreateDirectory(AppData.AppDataDir);
			if (!File.Exists(SettingsPath)) return new AppSettings();

			using var stream = File.OpenRead(SettingsPath);
			using var doc = JsonDocument.Parse(stream);
			return FromJson(doc.RootElement);
		} catch (Exception ex) {
			Log.Warn("Could not read settings; falling back to defaults.", ex);
			return new AppSettings();
		}
	}

	public static async Task<AppSettings> LoadAsync()
	{
		try {
			Directory.CreateDirectory(AppData.AppDataDir);
			if (!File.Exists(SettingsPath)) return new AppSettings();

			await using var stream = File.OpenRead(SettingsPath);
			using var doc = await JsonDocument.ParseAsync(stream);
			return FromJson(doc.RootElement);
		} catch (Exception ex) {
			Log.Warn("Could not read settings; falling back to defaults.", ex);
			return new AppSettings();
		}
	}

	private static AppSettings FromJson(JsonElement root)
	{
		var settings = new AppSettings();
		if (root.ValueKind != JsonValueKind.Object) return settings;

		settings.AccentColor = ReadString(root, "accentColor", settings.AccentColor);
		settings.EnableNotifications = ReadBool(root, "enableNotifications", settings.EnableNotifications);
		settings.EnableEELogRead = ReadBool(root, "enableEELogRead", settings.EnableEELogRead);
		settings.EnableRelicOverlay = ReadBool(root, "enableRelicOverlay", settings.EnableRelicOverlay);
		settings.EnableRivenOverlay = ReadBool(root, "enableRivenOverlay", settings.EnableRivenOverlay);
		settings.UIScale = ReadInt(root, "uiScale", settings.UIScale);
		settings.DebugOCR = ReadBool(root, "debugOCR", settings.DebugOCR);
		settings.OverlayOffset = ReadInt(root, "overlayOffset", settings.OverlayOffset);
		settings.Regions = ScanRegions.Read(root);
		return settings;
	}

	public async Task SaveAsync()
	{
		Directory.CreateDirectory(AppData.AppDataDir);

		await using var fs = File.Create(SettingsPath);
		await using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });

		writer.WriteStartObject();
		writer.WriteString("accentColor", string.IsNullOrWhiteSpace(AccentColor) ? defaultAccent : AccentColor);
		writer.WriteBoolean("enableNotifications", EnableNotifications);
		writer.WriteBoolean("enableEELogRead", EnableEELogRead);
		writer.WriteBoolean("enableRelicOverlay", EnableRelicOverlay);
		writer.WriteBoolean("enableRivenOverlay", EnableRivenOverlay);
		writer.WriteNumber("uiScale", UIScale);
		writer.WriteBoolean("debugOCR", DebugOCR);
		writer.WriteNumber("overlayOffset", OverlayOffset);
		Regions.Write(writer);
		writer.WriteEndObject();

		await writer.FlushAsync();
	}

	public void ApplyToApplicationResources()
	{
		if (Application.Current is null) return;
		if (!TryParseColor(string.IsNullOrWhiteSpace(AccentColor) ? defaultAccent : AccentColor, out var color)) return;

		Application.Current.Resources["AccentBrush"] = new SolidColorBrush(color);
	}

	private static bool TryParseColor(string value, out Color color) => Color.TryParse(value, out color);

	private static string ReadString(JsonElement root, string property, string fallback)
		=> root.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? fallback : fallback;

	private static bool ReadBool(JsonElement root, string property, bool fallback)
	{
		if (!root.TryGetProperty(property, out var p)) return fallback;
		return p.ValueKind switch {
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => fallback,
		};
	}

	private static int ReadInt(JsonElement root, string property, int fallback)
		=> root.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value) ? value : fallback;
}
