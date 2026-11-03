using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

internal readonly record struct MemoryScanResult(string? Authz, int Votes, string? Failure);

internal static class MemoryScanWorker
{
	private const string workerArgument = "--memory-scan-worker";
	private const int headerLength = 9;
	private const int maxPayloadLength = 16 * 1024;

	private static readonly TimeSpan operationTimeout = TimeSpan.FromSeconds(75);
	private static readonly TimeSpan terminationTimeout = TimeSpan.FromSeconds(3);
	private static readonly UTF8Encoding utf8 = new(false, true);

	public static bool IsWorkerMode(string[] args) => args.Length > 0 && string.Equals(args[0], workerArgument, StringComparison.Ordinal);

	public static int Run(string[] args)
	{
		MemoryScanResult result;
		try {
			if (args.Length != 2 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out int processId) || processId <= 0) {
				result = new MemoryScanResult(null, 0, "The memory scan worker received an invalid process id.");
			} else {
				result = WarframeApi.Scan(processId);
			}
		} catch (Exception ex) {
			result = new MemoryScanResult(null, 0, $"The memory scan worker failed: {ex.Message}");
		}

		try {
			WriteResponse(Console.OpenStandardOutput(), result);
			return 0;
		} catch {
			return 1;
		}
	}

	public static async Task<MemoryScanResult> ScanAsync(int processId)
	{
		ProcessStartInfo startInfo;
		try {
			startInfo = CreateStartInfo(processId);
		} catch (Exception ex) {
			return new MemoryScanResult(null, 0, $"Could not prepare the memory scan worker: {ex.Message}");
		}

		using var process = new Process { StartInfo = startInfo };
		try {
			if (!process.Start()) return new MemoryScanResult(null, 0, "Could not start the memory scan worker.");
		} catch (Exception ex) {
			return new MemoryScanResult(null, 0, $"Could not start the memory scan worker: {ex.Message}");
		}

		using var timeout = new CancellationTokenSource(operationTimeout);
		Task<MemoryScanResult> responseTask = ReadResponseAsync(process.StandardOutput.BaseStream, timeout.Token);

		try {
			await process.WaitForExitAsync(timeout.Token);
		} catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
			bool stopped = await StopAsync(process);
			await ObserveAsync(responseTask);
			return new MemoryScanResult(null, 0, stopped ? "Timed out waiting for the memory scan worker." : "Timed out waiting for the memory scan worker and could not terminate it.");
		} catch (Exception ex) {
			timeout.Cancel();
			bool stopped = await StopAsync(process);
			await ObserveAsync(responseTask);
			string cleanup = stopped ? "" : " The worker could not be terminated.";
			return new MemoryScanResult(null, 0, $"Could not wait for the memory scan worker: {ex.Message}{cleanup}");
		}

		if (process.ExitCode != 0) {
			await ObserveAsync(responseTask);
			return new MemoryScanResult(null, 0, $"The memory scan worker exited with code {process.ExitCode}.");
		}

		try {
			return await responseTask;
		} catch (Exception ex) {
			return new MemoryScanResult(null, 0, $"The memory scan worker returned an invalid response: {ex.Message}");
		}
	}

	private static ProcessStartInfo CreateStartInfo(int processId)
	{
		string executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("The application executable path is unavailable.");
		var startInfo = new ProcessStartInfo {
			FileName = executablePath,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
		};

		if (string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.OrdinalIgnoreCase)) {
			string entryAssemblyPath = Environment.GetCommandLineArgs()[0];
			if (!File.Exists(entryAssemblyPath)) throw new InvalidOperationException("The application assembly path is unavailable.");
			startInfo.ArgumentList.Add(Path.GetFullPath(entryAssemblyPath));
		}

		startInfo.ArgumentList.Add(workerArgument);
		startInfo.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
		return startInfo;
	}

	private static void WriteResponse(Stream stream, MemoryScanResult result)
	{
		bool success = result.Authz != null && result.Votes > 0;
		string value = success ? result.Authz! : result.Failure ?? "The game's API session token could not be found.";
		byte[] payload = utf8.GetBytes(value);
		if (payload.Length > maxPayloadLength) {
			success = false;
			payload = utf8.GetBytes("The memory scan worker produced an oversized response.");
		}

		Span<byte> header = stackalloc byte[headerLength];
		header[0] = success ? (byte)1 : (byte)0;
		BinaryPrimitives.WriteInt32LittleEndian(header[1..5], success ? result.Votes : 0);
		BinaryPrimitives.WriteInt32LittleEndian(header[5..9], payload.Length);
		stream.Write(header);
		stream.Write(payload);
		stream.Flush();
	}

	private static async Task<MemoryScanResult> ReadResponseAsync(Stream stream, CancellationToken token)
	{
		byte[] header = new byte[headerLength];
		await stream.ReadExactlyAsync(header, token);

		bool success = header[0] switch {
			0 => false,
			1 => true,
			_ => throw new InvalidDataException("Unknown response status."),
		};
		int votes = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
		int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(5, 4));
		if (payloadLength <= 0 || payloadLength > maxPayloadLength) throw new InvalidDataException("Invalid response length.");
		if (votes < 0 || (success ? votes == 0 : votes != 0)) throw new InvalidDataException("Invalid response vote count.");

		byte[] payload = new byte[payloadLength];
		await stream.ReadExactlyAsync(payload, token);
		byte[] trailing = new byte[1];
		if (await stream.ReadAsync(trailing, token) != 0) throw new InvalidDataException("Unexpected data after the response.");

		string value = utf8.GetString(payload);
		if (success && !IsValidToken(value)) throw new InvalidDataException("Invalid session token.");
		return success ? new MemoryScanResult(value, votes, null) : new MemoryScanResult(null, 0, value);
	}

	private static bool IsValidToken(string value)
	{
		const string accountMarker = "?accountId=";
		const string nonceMarker = "&nonce=";
		const int accountIdLength = 24;
		const int maxNonceLength = 32;

		if (!value.StartsWith(accountMarker, StringComparison.Ordinal)) return false;
		int nonceMarkerAt = accountMarker.Length + accountIdLength;
		if (value.Length <= nonceMarkerAt + nonceMarker.Length || !value.AsSpan(nonceMarkerAt, nonceMarker.Length).SequenceEqual(nonceMarker)) return false;

		foreach (char character in value.AsSpan(accountMarker.Length, accountIdLength)) {
			if (!char.IsAsciiLetterOrDigit(character)) return false;
		}

		ReadOnlySpan<char> nonce = value.AsSpan(nonceMarkerAt + nonceMarker.Length);
		if (nonce.Length > maxNonceLength) return false;
		foreach (char character in nonce) {
			if (!char.IsAsciiDigit(character)) return false;
		}
		return true;
	}

	private static async Task ObserveAsync(Task task)
	{
		try {
			await task;
		} catch {
		}
	}

	private static async Task<bool> StopAsync(Process process)
	{
		try {
			if (!process.HasExited) process.Kill();
		} catch {
		}

		try {
			using var timeout = new CancellationTokenSource(terminationTimeout);
			await process.WaitForExitAsync(timeout.Token);
			return true;
		} catch {
			try {
				return process.HasExited;
			} catch {
				return false;
			}
		}
	}
}
