using framenion.Src.Ocr;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

public class RelicRewardOCR
{
	/// <summary>
	/// Claimed atomically: the hotkey and the log monitor can both ask for a scan at the same
	/// moment, and a plain check-then-set let two of them past before either had claimed it.
	/// </summary>
	private static int running;

	public static bool OcrRunning => Volatile.Read(ref running) == 1;

	/// <summary>
	/// Log-driven entry point. The reward screen is known to be open here, so an empty read is
	/// worth reporting.
	/// </summary>
	public static void ReadRelicWindow() => _ = TryReadAsync(reportEmpty: true);

	/// <summary>
	/// Reads the reward screen. <paramref name="reportEmpty"/> is false when the caller is also
	/// going to try another reader and will decide for itself what to report.
	/// </summary>
	public static Task<ScanOutcome> TryReadAsync(bool reportEmpty)
	{
		if (!AppData.AppSettings.EnableRelicOverlay) return Task.FromResult(ScanOutcome.Disabled);
		if (Interlocked.Exchange(ref running, 1) == 1) return Task.FromResult(ScanOutcome.Unavailable);

		// Off the dispatcher: the capture and the OCR passes would otherwise stall the UI.
		return Task.Run(() => ScanAsync(reportEmpty));
	}

	private static async Task<ScanOutcome> ScanAsync(bool reportEmpty)
	{
		try {
			await Task.Delay(500);

			var engine = AppData.OcrEngine;
			if (engine == null) {
				Notify.Toast("OCR unavailable", OcrEngineFactory.UnavailableMessage);
				return ScanOutcome.Unavailable;
			}

			using var frame = ScreenCapture.Capture();
			var scanner = new RewardScanner(engine, RewardPool.Build(GameData.RewardNames)) {
				DebugDirectory = AppData.AppSettings.DebugOCR ? AppData.AppDataDir : null,
			};

			var scanned = await scanner.ScanAsync(frame.Image, AppData.AppSettings.UIScale / 100.0, AppData.AppSettings.Regions.RelicRewards);
			var rewards = scanned.Where(r => !r.Ignored).ToList();
			Log.Info($"[Rewards] Detected: {(rewards.Count == 0 ? "nothing" : string.Join(", ", scanned.Select(r => r.Ignored ? $"{r.Name} (ignored)" : r.Name)))}");
			if (rewards.Count == 0) {
				if (reportEmpty) Notify.Toast("No rewards detected", $"Could not detect any relic rewards. See {Log.Path}");
				return ScanOutcome.NotFound;
			}

			int cardWidth = (int)(240 * (AppData.AppSettings.UIScale / 100.0));
			var displayTasks = rewards.Select(reward => {
				int centerX = frame.OriginX + reward.TitleRect.X + (reward.TitleRect.Width / 2);
				int y = frame.OriginY + reward.TitleRect.Y + AppData.AppSettings.OverlayOffset;
				return RelicRewardWindow.Display(reward.Name, centerX, y, cardWidth);
			});
			await Task.WhenAll(displayTasks);
			return ScanOutcome.Found;
		} catch (Exception ex) {
			Log.Error("[Rewards] Scan failed.", ex);
			Notify.Error("Error", $"Failed to read rewards: {ex.Message}\n\nSee {Log.Path}");
			return ScanOutcome.Unavailable;
		} finally {
			Volatile.Write(ref running, 0);
		}
	}
}
