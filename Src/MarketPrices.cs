using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace framenion.Src;

/// <summary>A day's worth of market activity for one item.</summary>
/// <param name="Platinum">Representative price: what the item actually closed at where known.</param>
/// <param name="Volume">Units that changed hands, which is how much the price is worth trusting.</param>
/// <param name="FromClosedOrders">False when the figure is an asking price rather than a completed trade.</param>
public sealed record MarketQuote(double Platinum, int Volume, bool FromClosedOrders);

/// <summary>
/// Bulk market prices. warframe.market quotes one item per request, which cannot price a whole
/// inventory, so the numbers come from relics.run's daily dump of its statistics instead: one
/// file covering every item the market lists, mods, relics and arcanes included.
/// </summary>
internal static class MarketPrices
{
	private const string historyBaseUrl = "https://relics.run/history/price_history_";

	/// <summary>How far back to look. The dump for a given day appears some hours into it.</summary>
	private const int maxDaysBack = 5;

	private static string CachePath(DateTime date) => Path.Combine(AppData.CacheDir, $"price_history_{date:yyyy-MM-dd}.json");

	/// <summary>
	/// Loads the newest dump available, preferring the local cache when it is already current.
	/// Never throws: the inventory tab degrades to showing no prices.
	/// </summary>
	public static async Task<FrozenDictionary<string, MarketQuote>> LoadAsync()
	{
		try {
			var path = await EnsureCachedAsync();
			return path == null ? FrozenDictionary<string, MarketQuote>.Empty : await ParseAsync(path);
		} catch (Exception ex) {
			Log.Warn("Could not load bulk market prices; continuing without them.", ex);
			return FrozenDictionary<string, MarketQuote>.Empty;
		}
	}

	private static async Task<string?> EnsureCachedAsync()
	{
		Directory.CreateDirectory(AppData.CacheDir);
		var today = DateTime.UtcNow.Date;

		// Newest dump already on disk, so a run that cannot reach the network still has prices.
		string? cachedPath = null;
		var cachedDate = DateTime.MinValue;
		for (int back = 0; back <= maxDaysBack; back++) {
			var date = today.AddDays(-back);
			if (!File.Exists(CachePath(date))) continue;

			cachedPath = CachePath(date);
			cachedDate = date;
			break;
		}

		if (cachedDate == today) return cachedPath;

		for (int back = 0; back <= maxDaysBack; back++) {
			var date = today.AddDays(-back);
			// Stop once the candidates are no newer than what is already cached.
			if (cachedPath != null && date <= cachedDate) break;

			var destination = CachePath(date);
			try {
				await AppData.DownloadToFileAsync($"{historyBaseUrl}{date:yyyy-MM-dd}.json", destination);
				Prune(date);
				Log.Info($"Market price dump updated to {date:yyyy-MM-dd}.");
				return destination;
			} catch (Exception ex) {
				// The current day's dump is simply not published yet for most of the day.
				Log.Debug($"No market price dump for {date:yyyy-MM-dd}: {ex.Message}");
				try {
					if (File.Exists(destination)) File.Delete(destination);
				} catch {
					// best effort
				}
			}
		}

		if (cachedPath != null) Log.Info($"Reusing the cached market price dump from {cachedDate:yyyy-MM-dd}.");
		return cachedPath;
	}

	/// <summary>Drops superseded dumps; they are ~4 MB each and only the newest is ever read.</summary>
	private static void Prune(DateTime keep)
	{
		var keepName = Path.GetFileName(CachePath(keep));
		foreach (var file in Directory.EnumerateFiles(AppData.CacheDir, "price_history_*.json")) {
			if (string.Equals(Path.GetFileName(file), keepName, StringComparison.OrdinalIgnoreCase)) continue;

			try {
				File.Delete(file);
			} catch (Exception ex) {
				Log.Debug($"Could not remove the stale price dump '{file}': {ex.Message}");
			}
		}
	}

	private static async Task<FrozenDictionary<string, MarketQuote>> ParseAsync(string path)
	{
		await using var stream = File.OpenRead(path);
		using var doc = await JsonDocument.ParseAsync(stream);
		if (doc.RootElement.ValueKind != JsonValueKind.Object) {
			Log.Warn("The market price dump had an unexpected shape.");
			return FrozenDictionary<string, MarketQuote>.Empty;
		}

		// Keyed by the market's English display name, which is also how its item list labels items.
		var builder = new Dictionary<string, MarketQuote>(StringComparer.Ordinal);
		foreach (var item in doc.RootElement.EnumerateObject()) {
			if (item.Value.ValueKind != JsonValueKind.Array) continue;
			if (TryReadQuote(item.Value, out var quote)) builder[item.Name] = quote;
		}

		Log.Info($"Bulk market prices loaded: {builder.Count} items.");
		return builder.ToFrozenDictionary(StringComparer.Ordinal);
	}

	/// <summary>
	/// Reduces a day's order statistics to one number. Closed orders are trades that completed,
	/// so they win; sell orders are only what sellers are asking and read high, which is worth
	/// flagging rather than hiding.
	/// </summary>
	private static bool TryReadQuote(JsonElement entries, out MarketQuote quote)
	{
		double closed = 0, asking = 0;
		int closedVolume = 0, askingVolume = 0;

		foreach (var entry in entries.EnumerateArray()) {
			if (entry.ValueKind != JsonValueKind.Object) continue;

			double price = ReadNumber(entry, "median");
			if (price <= 0) price = ReadNumber(entry, "avg_price");
			if (price <= 0) price = ReadNumber(entry, "moving_avg");
			if (price <= 0) continue;

			int volume = (int)ReadNumber(entry, "volume");
			switch (entry.TryGetProperty("order_type", out var type) ? type.GetString() : null) {
				case "closed":
					closed = price;
					closedVolume = volume;
					break;
				case "sell":
					asking = price;
					askingVolume = volume;
					break;
			}
		}

		if (closed > 0) {
			quote = new MarketQuote(closed, closedVolume, true);
			return true;
		}

		if (asking > 0) {
			quote = new MarketQuote(asking, askingVolume, false);
			return true;
		}

		quote = default!;
		return false;
	}

	private static double ReadNumber(JsonElement element, string property)
		=> element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double parsed) && parsed > 0
			? parsed
			: 0;
}
