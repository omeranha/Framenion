using System;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

public enum ScanOutcome
{
	Disabled,
	Unavailable,
	NotFound,
	Found,
}

public static class ManualScan
{
	private static int running;

	public static void Trigger()
	{
		if (Interlocked.Exchange(ref running, 1) == 1) return;
		_ = RunAsync();
	}

	private static async Task RunAsync()
	{
		try {
			var riven = await Riven.RivenReader.TryRescanAsync(reportEmpty: false);
			if (riven == ScanOutcome.Found) return;

			var relic = await RelicRewardOCR.TryReadAsync(reportEmpty: false);
			if (relic == ScanOutcome.Found) return;

			Report(relic, riven);
		} catch (Exception ex) {
			Log.Error("[Scan] Manual scan failed.", ex);
			Notify.Error("Scan failed", $"{ex.Message}\n\nSee {Log.Path}");
		} finally {
			Volatile.Write(ref running, 0);
		}
	}

	private static void Report(ScanOutcome relic, ScanOutcome riven)
	{
		// A reader that could not run has already said so; adding "nothing found" on top of that would be two notifications for one key press.
		if (relic == ScanOutcome.Unavailable || riven == ScanOutcome.Unavailable) return;

		if (relic == ScanOutcome.Disabled && riven == ScanOutcome.Disabled) {
			Notify.Toast("Nothing to scan", "Both the relic reward and riven overlays are turned off in Settings.");
			return;
		}

		Log.Info($"[Scan] Hotkey found nothing (rewards: {relic}, riven: {riven}).");
		Notify.Toast("Nothing to scan", $"No relic rewards or riven card found on screen. See {Log.Path}");
	}
}
