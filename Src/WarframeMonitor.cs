using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace framenion.Src;

public class WarframeMonitor : IDisposable
{
	private readonly Timer timer;
	private CancellationTokenSource? logReadCts;
	private Task? logReadTask;

	private bool enableReader;
	private bool lastRunning;
	private uint warframeId;

	public event Action<bool>? OnProcessStateChanged;
	public event Action? OnRewardDetected;
	public event Action? OnSelectionClosed;

	public WarframeMonitor()
	{
		timer = new Timer(_ => FindProcess(), null, 0, 1500);
	}

	public void Start()
	{
		enableReader = true;
		if (lastRunning) {
			StartLogReader();
		}
	}

	public void Stop()
	{
		enableReader = false;
		StopLogReader();
	}

	private void FindProcess()
	{
		var processes = Process.GetProcessesByName("Warframe.x64");
		var process = processes.Length > 0 ? processes[0] : null;
		bool running = process != null;
		if (running == lastRunning) return;

		lastRunning = running;
		if (running) {
			warframeId = (uint)process!.Id;
			if (enableReader) {
				StartLogReader();
			}
		} else {
			warframeId = 0;
			StopLogReader();
		}

		Dispatcher.UIThread.Post(() =>
		{
			OnProcessStateChanged?.Invoke(running);
		});
	}

	private void StartLogReader()
	{
		if (logReadTask != null && !logReadTask.IsCompleted) return;

		logReadCts = new CancellationTokenSource();
		logReadTask = Task.Factory.StartNew(WindowsLogRead, logReadCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
	}

	private void StopLogReader()
	{
		if (logReadCts == null) return;

		logReadCts.Cancel();
		logReadCts.Dispose();
		logReadCts = null;
		logReadTask = null;
	}

	private void WindowsLogRead()
	{
		if (!OperatingSystem.IsWindows()) return;
		var token = logReadCts!.Token;

		using var mmf = MemoryMappedFile.CreateOrOpen("DBWIN_BUFFER", 4096);
		using var bufferReady = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_BUFFER_READY", out bool createdBuffer);
		if (!createdBuffer) return;

		using var dataReady = new EventWaitHandle(false, EventResetMode.AutoReset, "DBWIN_DATA_READY", out bool createdData);
		if (!createdData) return;

		byte[] buffer = new byte[4096];
		bufferReady.Set();
		var timeout = TimeSpan.FromSeconds(1.0);
		while (!token.IsCancellationRequested) {
			if (!dataReady.WaitOne(timeout)) {
				continue;
			}

			using var stream = mmf.CreateViewStream();
			int bytesRead = stream.Read(buffer, 0, buffer.Length);
			if (bytesRead < 4) {
				bufferReady.Set();
				continue;
			}

			uint pid = BitConverter.ToUInt32(buffer, 0);
			if (pid != warframeId) {
				bufferReady.Set();
				continue;
			}

			int length = 0;
			for (int i = 4; i < bytesRead; i++) {
				if (buffer[i] == 0) break;
				length++;
			}

			if (length > 0) {
				string message = Encoding.Default.GetString(buffer, 4, length);
				if (message.Contains("Got rewards") || message.Contains("Pause countdown done")) {
					OnRewardDetected?.Invoke();
				} else if (message.Contains("Relic reward screen shut down")) {
					OnSelectionClosed?.Invoke();
				}
			}
			bufferReady.Set();
		}
	}

	public void Dispose()
	{
		timer.Dispose();
		StopLogReader();
		GC.SuppressFinalize(this);
	}
}
