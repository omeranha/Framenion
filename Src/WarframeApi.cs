using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

/// <summary>
/// Fetches the player's inventory from the game's own mobile API, which needs the session token
/// the running client authenticated with. That token is not exposed anywhere, so it is recovered
/// by scanning the game's memory for the request URL it builds internally.
///
/// The token stays valid for as long as the client keeps its session, so the scan runs once per
/// game session and the result is cached in memory only; it is dropped when the game exits and
/// never written to disk or to the log, since it grants access to the account's data.
/// </summary>
public static class WarframeApi
{
	/// <summary>The query string the client assembles, e.g. "?accountId=&lt;24 chars&gt;&amp;nonce=&lt;digits&gt;".</summary>
	private static ReadOnlySpan<byte> AccountIdMarker => "?accountId="u8;
	private static ReadOnlySpan<byte> NonceMarker => "&nonce="u8;

	private const int accountIdLength = 24;
	private const int maxNonceLength = 32;

	/// <summary>
	/// How many identical readings end the scan early. The pattern also occurs in half-built and
	/// freed copies of the URL, so a reading only counts once others agree with it.
	/// </summary>
	private const int agreementVotes = 3;
	private const int minimumVotes = 2;

	private const int chunkSize = 1 << 20;

	/// <summary>Carried past the end of each chunk so a URL straddling the boundary stays whole.</summary>
	private const int recordSlack = 128;

	private static readonly TimeSpan scanTimeout = TimeSpan.FromSeconds(60);

	private static readonly HttpClient http = new() {
		BaseAddress = new Uri("https://mobile.warframe.com/"),
		Timeout = TimeSpan.FromSeconds(90),
		DefaultRequestHeaders = { { "User-Agent", "Framenion" } },
	};

	private static readonly object cacheGate = new();

	/// <summary>Serializes the scan so two refreshes in a row cannot both pay for it.</summary>
	private static readonly SemaphoreSlim scanGate = new(1, 1);

	private static int cachedProcessId;
	private static string? cachedAuthz;
	private static bool consentGiven;

	private enum Outcome
	{
		Success,

		/// <summary>The API answered, but not with an inventory: the token is no longer good.</summary>
		Rejected,
		Failed,
	}

	/// <summary>
	/// Drops the cached token. Called when the game process goes away, since the next client will
	/// authenticate with a token of its own.
	/// </summary>
	public static void InvalidateSession()
	{
		lock (cacheGate) {
			if (cachedAuthz == null && cachedProcessId == 0 && !consentGiven) return;

			cachedProcessId = 0;
			cachedAuthz = null;
			consentGiven = false;
		}
		Log.Info("Game session ended; discarded the cached API session token.");
	}

	/// <summary>
	/// Forgets the token but keeps the consent already given, so a rescan within the same game
	/// session does not ask the user a second time.
	/// </summary>
	private static void DropCachedToken()
	{
		lock (cacheGate) {
			cachedProcessId = 0;
			cachedAuthz = null;
		}
	}

	/// <summary>
	/// Downloads the inventory to <see cref="Inventory.FilePath"/>. Reports its own failures and
	/// leaves any previously downloaded inventory untouched when it cannot get a usable response.
	/// </summary>
	public static async Task FetchInventoryAsync()
	{
		int processId = WarframeMonitor.FindProcessId();
		if (processId == 0) {
			Notify.Error("Warframe is not running", "Start Warframe and log in, then refresh again.");
			return;
		}

		// One retry covers an in-game relog, which rotates the token without restarting the
		// process, so the cached reading is stale even though the session looks unchanged.
		for (int attempt = 0; attempt < 2; attempt++) {
			string? authz = await GetSessionTokenAsync(processId);
			if (authz == null) return;

			var (outcome, message) = await DownloadInventoryAsync(authz);
			if (outcome == Outcome.Success) {
				Log.Info("Inventory downloaded from the game API.");
				return;
			}

			bool canRetry = outcome == Outcome.Rejected && attempt == 0;
			if (!canRetry) {
				Notify.Error("Could not read your inventory", message);
				return;
			}

			Log.Info("The game API rejected the cached session token; scanning once more.");
			DropCachedToken();
		}
	}

	/// <summary>Returns the session's token, scanning for it on first use. Null when unavailable.</summary>
	private static async Task<string?> GetSessionTokenAsync(int processId)
	{
		if (TryGetCached(processId) is string cached) return cached;

		await scanGate.WaitAsync();
		try {
			// Another refresh may have finished the scan while this one queued.
			if (TryGetCached(processId) is string justScanned) return justScanned;

			if (!await ConfirmConsentAsync()) {
				Notify.Toast("Info", "Inventory refresh cancelled.");
				return null;
			}

			var (authz, votes, failure) = await MemoryScanWorker.ScanAsync(processId);
			if (authz == null) {
				string message = failure ?? "The game's API session token could not be found.";
				Log.Warn($"Memory scan worker failed: {message}");
				Notify.Error("Could not read your inventory", message);
				return null;
			}

			Log.Info($"Recovered the API session token from game memory in a separate process ({votes} agreeing readings).");
			lock (cacheGate) {
				cachedProcessId = processId;
				cachedAuthz = authz;
			}
			return authz;
		} finally {
			scanGate.Release();
		}
	}

	/// <summary>
	/// The cached token is keyed by process id, so a client that restarted between two of the
	/// monitor's polls cannot be handed the previous session's token.
	/// </summary>
	private static string? TryGetCached(int processId)
	{
		lock (cacheGate) {
			return cachedProcessId == processId ? cachedAuthz : null;
		}
	}

	private static async Task<bool> ConfirmConsentAsync()
	{
		lock (cacheGate) {
			if (consentGiven) return true;
		}

		bool accepted = await Notify.Confirm(
			"Read inventory from Warframe",
			"Framenion will start a temporary local worker process to read the running game's memory and recover the session token needed to request your inventory from Warframe's own API. " +
			"The read is local and read-only, the worker exits when the scan finishes, and the token stays in memory only until you close the game. Continue at your own risk.",
			"Confirm",
			"Cancel");

		if (accepted) {
			lock (cacheGate) {
				consentGiven = true;
			}
		}
		return accepted;
	}

	internal static MemoryScanResult Scan(int processId)
	{
		using var memory = ProcessMemory.TryOpen(processId, out var failure);
		if (memory == null) return new MemoryScanResult(null, 0, failure ?? "Could not open the game process.");

		using var timeout = new CancellationTokenSource(scanTimeout);
		var (authz, votes) = FindSessionToken(memory, timeout.Token);
		if (authz != null) return new MemoryScanResult(authz, votes, null);

		return new MemoryScanResult(null, 0, timeout.IsCancellationRequested
			? "Timed out searching the game's memory for its API session token. Try refreshing again."
			: "Could not find the game's API session token. Make sure you are logged in to Warframe, then try again.");
	}

	/// <summary>
	/// Sweeps the game's readable memory for the inventory request URL. Readings are tallied
	/// rather than trusted on sight: the same URL appears in several places once the client is
	/// logged in, while fragments of older ones linger in freed memory.
	/// </summary>
	private static (string? Authz, int Votes) FindSessionToken(ProcessMemory memory, CancellationToken token)
	{
		int minimumRecord = AccountIdMarker.Length + accountIdLength + NonceMarker.Length + 2;
		var votes = new Dictionary<string, int>(StringComparer.Ordinal);
		byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize + recordSlack);

		try {
			foreach (var region in memory.EnumerateRegions()) {
				for (ulong offset = 0; offset < region.Size; offset += chunkSize) {
					if (token.IsCancellationRequested) return (null, 0);

					int want = (int)Math.Min((ulong)(chunkSize + recordSlack), region.Size - offset);
					int read = memory.Read(region.Address + offset, buffer.AsSpan(0, want));
					if (read < minimumRecord) continue;

					// The slack belongs to the following chunk, which scans it as its own.
					int owned = Math.Min(read, chunkSize);
					if (Tally(buffer.AsSpan(0, read), owned, votes) is string settled) {
						return (settled, agreementVotes);
					}
				}
			}
		} finally {
			ArrayPool<byte>.Shared.Return(buffer);
		}

		return BestReading(votes);
	}

	/// <summary>Votes for every well-formed URL in the chunk; returns one that has settled.</summary>
	private static string? Tally(ReadOnlySpan<byte> data, int ownedLength, Dictionary<string, int> votes)
	{
		for (int from = 0; from < ownedLength;) {
			int at = data[from..].IndexOf(AccountIdMarker);
			if (at < 0) break;

			int start = from + at;
			if (start >= ownedLength) break;

			if (TryReadToken(data[start..], out string authz)) {
				votes.TryGetValue(authz, out int count);
				votes[authz] = ++count;
				if (count >= agreementVotes) return authz;
			}
			from = start + 1;
		}
		return null;
	}

	/// <summary>
	/// Reads one "?accountId=...&amp;nonce=..." URL. Every field is checked rather than assumed,
	/// because the marker also turns up inside unrelated buffers.
	/// </summary>
	private static bool TryReadToken(ReadOnlySpan<byte> data, out string authz)
	{
		authz = "";

		int accountIdAt = AccountIdMarker.Length;
		int nonceMarkerAt = accountIdAt + accountIdLength;
		int nonceAt = nonceMarkerAt + NonceMarker.Length;
		if (data.Length <= nonceAt) return false;

		var accountId = data.Slice(accountIdAt, accountIdLength);
		foreach (byte c in accountId) {
			if (!char.IsAsciiLetterOrDigit((char)c)) return false;
		}

		if (!data.Slice(nonceMarkerAt, NonceMarker.Length).SequenceEqual(NonceMarker)) return false;

		var tail = data[nonceAt..];
		int digits = 0;
		while (digits < tail.Length && digits <= maxNonceLength && char.IsAsciiDigit((char)tail[digits])) digits++;

		// Only accept a nonce whose terminator was in view: a digit run that stops at the edge of
		// what was read may be the front of a longer number, and would be voted for as-is.
		if (digits == 0 || digits > maxNonceLength || digits == tail.Length) return false;

		authz = Encoding.ASCII.GetString(data[..(nonceAt + digits)]);
		return true;
	}

	/// <summary>The clear winner once the sweep ends without one reaching <see cref="agreementVotes"/>.</summary>
	private static (string? Authz, int Votes) BestReading(Dictionary<string, int> votes)
	{
		string? best = null;
		int bestCount = 0;
		bool tied = false;

		foreach (var (authz, count) in votes) {
			if (count > bestCount) {
				best = authz;
				bestCount = count;
				tied = false;
			} else if (count == bestCount) {
				tied = true;
			}
		}

		return bestCount >= minimumVotes && !tied ? (best, bestCount) : (null, 0);
	}

	private static async Task<(Outcome Outcome, string Message)> DownloadInventoryAsync(string authz)
	{
		try {
			using var response = await http.GetAsync("api/inventory.php" + authz);
			if (!response.IsSuccessStatusCode) {
				int status = (int)response.StatusCode;
				// Only an auth refusal means the token itself is worth re-reading. Rate limits and
				// outages are not, so they must not cost another full memory sweep.
				return status is 401 or 403
					? (Outcome.Rejected, $"Warframe's API rejected the session token ({status}).")
					: (Outcome.Failed, $"Warframe's API returned {status} {response.ReasonPhrase}. Try again later.");
			}

			string body = await response.Content.ReadAsStringAsync();
			return TryStoreInventory(body)
				? (Outcome.Success, "")
				: (Outcome.Rejected, "Warframe's API did not return an inventory. Make sure you are logged in to the game, then try again.");
		} catch (TaskCanceledException) {
			return (Outcome.Failed, "The request to Warframe's API timed out.");
		} catch (Exception ex) {
			Log.Error("Inventory download failed.", ex);
			return (Outcome.Failed, $"Could not reach Warframe's API: {ex.Message}");
		}
	}

	/// <summary>
	/// Replaces the stored inventory, but only once the response is recognizable as one. An error
	/// payload parses as JSON perfectly well, and writing it would destroy the last good download.
	/// </summary>
	private static bool TryStoreInventory(string body)
	{
		JsonDocument document;
		try {
			document = JsonDocument.Parse(body);
		} catch (JsonException) {
			return false;
		}

		using (document) {
			var root = document.RootElement;
			if (root.ValueKind != JsonValueKind.Object) return false;
			if (!root.TryGetProperty("XPInfo", out _) && !root.TryGetProperty("MiscItems", out _) && !root.TryGetProperty("Recipes", out _)) return false;

			Directory.CreateDirectory(AppData.AppDataDir);

			// Staged, so an interrupted write cannot leave a truncated inventory behind.
			string temp = Inventory.FilePath + ".part";
			try {
				using (var file = File.Create(temp))
				using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true })) {
					document.WriteTo(writer);
				}
				File.Move(temp, Inventory.FilePath, overwrite: true);
			} catch (Exception ex) {
				Log.Error("Could not save the downloaded inventory.", ex);
				try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
				return false;
			}
		}

		return true;
	}
}
