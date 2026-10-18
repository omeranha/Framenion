using System;
using System.Text.Json;

namespace framenion.Src;

/// <summary>
/// A screen region stored as fractions of the captured frame, so one setup survives a
/// resolution change.
/// </summary>
public readonly record struct ScanRegion(double X, double Y, double Width, double Height)
{
	public double Right => X + Width;
	public double Bottom => Y + Height;

	public bool IsValid =>
		double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
		&& X >= 0 && Y >= 0 && Width > 0.01 && Height > 0.005
		&& X + Width <= 1.0001 && Y + Height <= 1.0001;

	public ScanRegion Clamped()
	{
		double width = Math.Clamp(Width, 0.01, 1);
		double height = Math.Clamp(Height, 0.005, 1);
		double x = Math.Clamp(X, 0, 1 - width);
		double y = Math.Clamp(Y, 0, 1 - height);
		return new ScanRegion(x, y, width, height);
	}

	public override string ToString()
		=> $"x {X * 100:0.0}%  y {Y * 100:0.0}%  w {Width * 100:0.0}%  h {Height * 100:0.0}%";
}

public enum ScanRegionKind
{
	/// <summary>The row of reward card names on the relic reward screen.</summary>
	RelicRewards,

	/// <summary>The stat block of the riven card on the Kuva reroll screen.</summary>
	RivenReroll,

	/// <summary>The plate naming the weapon on the reroll screen, right of the card.</summary>
	RivenRerollWeapon,

	/// <summary>The stat block of a riven linked in chat.</summary>
	RivenChat,

	/// <summary>The weapon name beside a chat-linked riven.</summary>
	RivenChatWeapon,
}

/// <summary>
/// Optional user overrides for the scan regions. Null means "use the measured defaults", which
/// adapt to aspect ratio and UI scale; an override is used verbatim.
/// </summary>
public sealed class ScanRegions
{
	public ScanRegion? RelicRewards { get; set; }
	public ScanRegion? RivenReroll { get; set; }
	public ScanRegion? RivenRerollWeapon { get; set; }
	public ScanRegion? RivenChat { get; set; }
	public ScanRegion? RivenChatWeapon { get; set; }

	// Reference values shown in the editor and restored by "Use default". These are the
	// measured geometry evaluated at 16:9, which is what the automatic path produces there.
	public static readonly ScanRegion DefaultRelicRewards = new(0.245, 0.3825, 0.503, 0.0405);
	public static readonly ScanRegion DefaultRivenReroll = new(0.412, 0.583, 0.176, 0.257);
	public static readonly ScanRegion DefaultRivenChat = new(0.412, 0.453, 0.176, 0.225);

	// The weapon that decides the riven's class is named to the right of the card on the reroll
	// screen's "FITS IN" plate. A chat link has no such plate: the weapon is the first word of
	// the mod's own name, printed as the card's title.
	public static readonly ScanRegion DefaultRivenRerollWeapon = new(0.78, 0.72, 0.21, 0.22);
	public static readonly ScanRegion DefaultRivenChatWeapon = new(0.395, 0.345, 0.21, 0.09);

	/// <summary>Order shown in the editor: each riven layout followed by its weapon plate.</summary>
	public static readonly ScanRegionKind[] EditOrder = [
		ScanRegionKind.RelicRewards,
		ScanRegionKind.RivenReroll,
		ScanRegionKind.RivenRerollWeapon,
		ScanRegionKind.RivenChat,
		ScanRegionKind.RivenChatWeapon,
	];

	public static ScanRegion DefaultFor(ScanRegionKind kind) => kind switch {
		ScanRegionKind.RelicRewards => DefaultRelicRewards,
		ScanRegionKind.RivenReroll => DefaultRivenReroll,
		ScanRegionKind.RivenRerollWeapon => DefaultRivenRerollWeapon,
		ScanRegionKind.RivenChat => DefaultRivenChat,
		_ => DefaultRivenChatWeapon,
	};

	public ScanRegion? Get(ScanRegionKind kind) => kind switch {
		ScanRegionKind.RelicRewards => RelicRewards,
		ScanRegionKind.RivenReroll => RivenReroll,
		ScanRegionKind.RivenRerollWeapon => RivenRerollWeapon,
		ScanRegionKind.RivenChat => RivenChat,
		_ => RivenChatWeapon,
	};

	public void Set(ScanRegionKind kind, ScanRegion? region)
	{
		switch (kind) {
			case ScanRegionKind.RelicRewards: RelicRewards = region; break;
			case ScanRegionKind.RivenReroll: RivenReroll = region; break;
			case ScanRegionKind.RivenRerollWeapon: RivenRerollWeapon = region; break;
			case ScanRegionKind.RivenChat: RivenChat = region; break;
			default: RivenChatWeapon = region; break;
		}
	}

	public static string Label(ScanRegionKind kind) => kind switch {
		ScanRegionKind.RelicRewards => "Relic reward names",
		ScanRegionKind.RivenReroll => "Riven stats (reroll screen)",
		ScanRegionKind.RivenRerollWeapon => "Riven weapon / class (reroll screen)",
		ScanRegionKind.RivenChat => "Riven stats (chat link)",
		_ => "Riven weapon / class (chat link)",
	};

	public static string Hint(ScanRegionKind kind) => kind switch {
		ScanRegionKind.RelicRewards => "Draw a box around the row of reward names, not the whole cards.",
		ScanRegionKind.RivenRerollWeapon =>
			"Draw a box around the weapon name on the FITS IN plate, right of the card. This decides the riven's class and disposition, so the grade depends on it.",
		ScanRegionKind.RivenChatWeapon =>
			"Draw a box around the card's title. A chat-linked riven names its weapon there, and that decides the class and disposition, so the grade depends on it.",
		_ => "Draw a box around the riven's stat lines, excluding the title and the MR footer.",
	};

	public ScanRegions Clone() => new() {
		RelicRewards = RelicRewards,
		RivenReroll = RivenReroll,
		RivenRerollWeapon = RivenRerollWeapon,
		RivenChat = RivenChat,
		RivenChatWeapon = RivenChatWeapon,
	};

	public void Write(Utf8JsonWriter writer)
	{
		writer.WriteStartObject("scanRegions");
		WriteRegion(writer, "relicRewards", RelicRewards);
		WriteRegion(writer, "rivenReroll", RivenReroll);
		WriteRegion(writer, "rivenRerollWeapon", RivenRerollWeapon);
		WriteRegion(writer, "rivenChat", RivenChat);
		WriteRegion(writer, "rivenChatWeapon", RivenChatWeapon);
		writer.WriteEndObject();
	}

	private static void WriteRegion(Utf8JsonWriter writer, string name, ScanRegion? region)
	{
		if (region == null) return;

		writer.WriteStartObject(name);
		writer.WriteNumber("x", Math.Round(region.Value.X, 5));
		writer.WriteNumber("y", Math.Round(region.Value.Y, 5));
		writer.WriteNumber("width", Math.Round(region.Value.Width, 5));
		writer.WriteNumber("height", Math.Round(region.Value.Height, 5));
		writer.WriteEndObject();
	}

	public static ScanRegions Read(JsonElement root)
	{
		var regions = new ScanRegions();
		if (!root.TryGetProperty("scanRegions", out var element) || element.ValueKind != JsonValueKind.Object) {
			return regions;
		}

		regions.RelicRewards = ReadRegion(element, "relicRewards");
		regions.RivenReroll = ReadRegion(element, "rivenReroll");
		regions.RivenRerollWeapon = ReadRegion(element, "rivenRerollWeapon");
		regions.RivenChat = ReadRegion(element, "rivenChat");
		regions.RivenChatWeapon = ReadRegion(element, "rivenChatWeapon");
		return regions;
	}

	private static ScanRegion? ReadRegion(JsonElement parent, string name)
	{
		if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object) return null;

		var region = new ScanRegion(
			ReadNumber(element, "x"),
			ReadNumber(element, "y"),
			ReadNumber(element, "width"),
			ReadNumber(element, "height"));

		// A malformed override would silently break scanning, so fall back to automatic.
		if (!region.IsValid) {
			Log.Warn($"Ignoring invalid saved scan region '{name}': {region}");
			return null;
		}
		return region;
	}

	private static double ReadNumber(JsonElement element, string name)
		=> element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.NaN;
}
