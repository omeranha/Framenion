using System;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

/// <summary>Why a scan produced no overlay, which decides whether it is worth telling the user.</summary>
public enum ScanOutcome
{
	/// <summary>The feature is switched off, so not scanning is the expected behaviour.</summary>
	Disabled,

	/// <summary>Could not scan at all. The reader has already explained why.</summary>
	Unavailable,

	/// <summary>Scanned, and this screen holds nothing for this reader.</summary>
	NotFound,

	Found,
}

/// <summary>
/// The single scan hotkey. It is not bound to a particular screen, so both readers are tried and
/// exactly one of them is expected to come back empty every time. Neither reports that on its
/// own any more; one message is shown here, and only when nothing at all was found.
/// </summary>
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
			// Relics first, and short-circuited: it is the common case, and skipping the riven
			// pass also skips its retry delay when there is nothing on screen to retry for.
			var relic = await RelicRewardOCR.TryReadAsync(reportEmpty: false);
			if (relic == ScanOutcome.Found) return;

			var riven = await Riven.RivenReader.TryRescanAsync(reportEmpty: false);
			if (riven == ScanOutcome.Found) return;

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
		// A reader that could not run has already said so; adding "nothing found" on top of that
		// would be two notifications for one key press.
		if (relic == ScanOutcome.Unavailable || riven == ScanOutcome.Unavailable) return;

		if (relic == ScanOutcome.Disabled && riven == ScanOutcome.Disabled) {
			Notify.Toast("Nothing to scan", "Both the relic reward and riven overlays are turned off in Settings.");
			return;
		}

		Log.Info($"[Scan] Hotkey found nothing (rewards: {relic}, riven: {riven}).");
		Notify.Toast("Nothing to scan", $"No relic rewards or riven card found on screen. See {Log.Path}");
	}
}
