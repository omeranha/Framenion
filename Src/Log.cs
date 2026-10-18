using System;
using System.IO;
using System.Text;

namespace framenion.Src;

public enum LogLevel
{
	Debug,
	Info,
	Warn,
	Error,
}

/// <summary>
/// Minimal rolling file log. Failures in the app are mostly surfaced as modal dialogs with a
/// single line of text, which is not enough to diagnose anything after the fact, so every
/// swallowed exception should at least land here.
/// </summary>
public static class Log
{
	private const long maxBytes = 1024 * 1024;

	private static readonly object gate = new();
	private static string? path;

	public static string Path => path ??= System.IO.Path.Combine(AppData.AppDataDir, "framenion.log");

	public static void Debug(string message) => Write(LogLevel.Debug, message, null);

	public static void Info(string message) => Write(LogLevel.Info, message, null);

	public static void Warn(string message, Exception? error = null) => Write(LogLevel.Warn, message, error);

	public static void Error(string message, Exception? error = null) => Write(LogLevel.Error, message, error);

	private static void Write(LogLevel level, string message, Exception? error)
	{
		try {
			var line = new StringBuilder(message.Length + 64);
			line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
			line.Append(" [").Append(level.ToString().ToUpperInvariant()).Append("] ");
			line.Append(message);
			if (error != null) {
				line.AppendLine().Append("    ").Append(error.GetType().Name).Append(": ").Append(error.Message);
				if (error.StackTrace != null) {
					line.AppendLine().Append(error.StackTrace);
				}
			}

			lock (gate) {
				Directory.CreateDirectory(AppData.AppDataDir);
				Rotate();
				File.AppendAllText(Path, line.AppendLine().ToString());
			}
		} catch {
			// Logging must never take the app down.
		}
	}

	private static void Rotate()
	{
		var info = new FileInfo(Path);
		if (!info.Exists || info.Length < maxBytes) return;

		var previous = Path + ".1";
		if (File.Exists(previous)) File.Delete(previous);
		File.Move(Path, previous);
	}
}
