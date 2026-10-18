using framenion.Src.Ocr;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src.Riven;

/// <summary>
/// Drives riven scans: the reroll screen opening, each confirmed roll, and the manual hotkey.
/// </summary>
public static class RivenReader
{
	/// <summary>Let the card finish animating in before the first capture.</summary>
	private const int initialScanDelayMs = 250;

	/// <summary>A roll plays a reveal animation before the new values settle.</summary>
	private const int rollScanDelayMs = 2850;

	/// <summary>Retry delay when a capture came back short.</summary>
	private const int retryDelayMs = 700;

	private const int maxAttempts = 2;

	private static readonly SemaphoreSlim gate = new(1, 1);
	private static int generation;

	/// <summary>True while the reroll screen is open, so the hotkey knows which layout to use.</summary>
	public static bool SessionOpen { get; private set; }

	public static void OnSessionOpen()
	{
		SessionOpen = true;
		Queue(RivenScanLayout.Reroll, initialScanDelayMs);
	}

	public static void OnRolled() => Queue(RivenScanLayout.Roll, rollScanDelayMs);

	public static void OnSessionClose()
	{
		SessionOpen = false;
		Interlocked.Increment(ref generation);
		RivenOverlayWindow.HideOverlay();
	}

	/// <summary>
	/// Manual rescan. Tries the reroll layout then the chat one rather than trusting the session
	/// flag: the log marker that sets it does not always arrive, and guessing wrong meant the
	/// hotkey scanned entirely the wrong part of the screen.
	/// </summary>
	public static void Rescan() => Queue(SessionOpen
		? [RivenScanLayout.Reroll, RivenScanLayout.Chat]
		: [RivenScanLayout.Chat, RivenScanLayout.Reroll], 0);

	private static void Queue(RivenScanLayout layout, int delayMs) => Queue([layout], delayMs);

	private static void Queue(RivenScanLayout[] layouts, int delayMs)
	{
		if (!AppData.AppSettings.EnableRivenOverlay) return;

		int token = Interlocked.Increment(ref generation);
		_ = Task.Run(async () => {
			try {
				if (delayMs > 0) await Task.Delay(delayMs);
				if (Volatile.Read(ref generation) != token) return;

				await ScanAsync(layouts, token);
			} catch (Exception ex) {
				Log.Error("[Riven] Scan failed.", ex);
				Notify.Error("Riven scan failed", $"{ex.Message}\n\nSee {Log.Path}");
			}
		});
	}

	private static async Task ScanAsync(RivenScanLayout[] layouts, int token)
	{
		var engine = AppData.OcrEngine;
		if (engine == null) {
			Notify.Toast("Riven reader", OcrEngineFactory.UnavailableMessage);
			return;
		}

		if (!RivenData.IsAvailable) {
			Notify.Toast("Riven reader", "Riven data is still loading.");
			return;
		}

		await gate.WaitAsync();
		try {
			var scanner = new RivenScanner(engine) {
				DebugDirectory = AppData.AppSettings.DebugOCR ? AppData.AppDataDir : null,
			};

			RivenScanResult? best = null;
			int originX = 0, originY = 0;

			for (int attempt = 0; attempt < maxAttempts && best?.Grade == null; attempt++) {
				if (attempt > 0) await Task.Delay(retryDelayMs);

				using var frame = ScreenCapture.Capture();
				foreach (var layout in layouts) {
					if (Volatile.Read(ref generation) != token) return;

					var result = await scanner.ScanAsync(frame.Image, layout);
					if (result == null) continue;

					Log.Debug($"[Riven] {layout} layout: {result.Stats.Count} stat(s), graded={result.Grade != null}");
					if (best == null || result.Stats.Count > best.Stats.Count || (result.Grade != null && best.Grade == null)) {
						best = result;
						originX = frame.OriginX;
						originY = frame.OriginY;
					}

					if (best.Grade != null) break;
				}
			}

			if (Volatile.Read(ref generation) != token) return;

			if (best == null || best.Stats.Count == 0) {
				Log.Info("[Riven] No riven card detected.");
				Notify.Toast("No riven detected", $"Could not read a riven card on screen. See {Log.Path}");
				return;
			}

			Log.Info($"[Riven] {best.WeaponName}: {string.Join(", ", best.Stats.Select(s => $"{(s.Positive ? "+" : "-")}{s.Value?.ToString() ?? "?"} {s.Name}"))}");
			RivenOverlayWindow.Display(best, originX, originY);
		} finally {
			gate.Release();
		}
	}
}
