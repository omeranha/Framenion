using System;
using System.Threading.Tasks;

namespace framenion.Src;

/// <summary>
/// Indirection between the data layer and the windows that present its messages. Keeps
/// PublicExport/GameData free of UI references and lets them run headless; the defaults simply
/// log, and the main window swaps in the real toast and dialog implementations at startup.
/// </summary>
public static class Notify
{
	public static Action<string, string> Toast { get; set; } = (title, body) => Log.Info($"{title}: {body}");

	public static Action<string, string> Error { get; set; } = (title, body) => Log.Error($"{title}: {body}");

	public static Func<string, string, string, string, Task<bool>> Confirm { get; set; } =
		(title, body, ok, cancel) => {
			Log.Warn($"Confirmation requested with no UI attached, declining: {title} - {body}");
			return Task.FromResult(false);
		};
}
