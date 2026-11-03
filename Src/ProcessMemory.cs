using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace framenion.Src;

public readonly record struct MemoryRegion(ulong Address, ulong Size);

/// <summary>
/// Read-only access to another process's memory, used to recover the game's API session token.
/// Opened with the narrowest rights each platform offers and with no write path at all, so a bug
/// here cannot alter the game's state: there is deliberately no counterpart to <see cref="Read"/>.
/// Every failure is reported rather than thrown, since a live process frees memory mid-scan and
/// partial failures are routine.
/// </summary>
public abstract class ProcessMemory : IDisposable
{
	/// <summary>Returns null when the process is gone or the OS refuses access.</summary>
	public static ProcessMemory? TryOpen(int processId, out string? failure)
	{
		failure = null;
		try {
			if (OperatingSystem.IsWindows()) return WindowsMemory.Open(processId, out failure);
			if (OperatingSystem.IsLinux()) return LinuxMemory.Open(processId, out failure);
			failure = "Reading game memory is not supported on this platform.";
		} catch (Exception ex) {
			failure = $"Could not open the game process: {ex.Message}";
		}
		return null;
	}

	/// <summary>
	/// Committed, readable regions, lazily. Only anonymous and mapped memory is reported: the
	/// token is assembled at runtime so it lives on the heap, and skipping the executable's own
	/// images keeps a multi-gigabyte scan to the pages that can actually hold it.
	/// </summary>
	public abstract IEnumerable<MemoryRegion> EnumerateRegions();

	/// <summary>Fills as much of <paramref name="buffer"/> as it can; returns the byte count.</summary>
	public abstract int Read(ulong address, Span<byte> buffer);

	public abstract void Dispose();

	[SupportedOSPlatform("windows")]
	private sealed class WindowsMemory : ProcessMemory
	{
		private const uint processQueryInformation = 0x0400;
		private const uint processVmRead = 0x0010;

		private const uint memCommit = 0x1000;
		private const uint memPrivate = 0x20000;
		private const uint memMapped = 0x40000;

		private const uint pageGuard = 0x100;
		private const uint readableProtections = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

		// Highest user-mode address on x64; the kernel half is never readable from here.
		private const ulong userAddressLimit = 0x7FFFFFFFFFFF;

		private readonly SafeProcessHandle handle;

		private WindowsMemory(SafeProcessHandle handle) => this.handle = handle;

		public static ProcessMemory? Open(int processId, out string? failure)
		{
			failure = null;
			var handle = OpenProcess(processQueryInformation | processVmRead, false, (uint)processId);
			if (!handle.IsInvalid) return new WindowsMemory(handle);

			int error = Marshal.GetLastWin32Error();
			handle.Dispose();
			failure = error == 5
				? "Access to the game process was denied. If Warframe is running as administrator, start Framenion as administrator too."
				: $"Could not open the game process (error {error}).";
			return null;
		}

		public override IEnumerable<MemoryRegion> EnumerateRegions()
		{
			ulong address = 0;
			while (address < userAddressLimit && !handle.IsClosed) {
				if (VirtualQueryEx(handle, (nint)address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0) break;

				ulong regionBase = info.BaseAddress;
				ulong size = info.RegionSize;
				if (size == 0) break;

				bool usable = info.State == memCommit
					&& (info.Type == memPrivate || info.Type == memMapped)
					&& (info.Protect & pageGuard) == 0
					&& (info.Protect & readableProtections) != 0;
				if (usable) yield return new MemoryRegion(regionBase, size);

				// The walk must always advance, or a bogus region size would spin forever.
				ulong next = regionBase + size;
				if (next <= address) break;
				address = next;
			}
		}

		public override int Read(ulong address, Span<byte> buffer)
		{
			if (buffer.Length == 0) return 0;

			// A failed call can still have copied the leading pages, which is worth keeping:
			// regions routinely end in a page the game has just released.
			bool ok = ReadProcessMemory(handle, (nint)address, out buffer[0], (nuint)buffer.Length, out nuint read);
			int copied = (int)Math.Min((ulong)read, (ulong)buffer.Length);
			return ok || copied > 0 ? copied : 0;
		}

		public override void Dispose() => handle.Dispose();

		[StructLayout(LayoutKind.Sequential)]
		private struct MemoryBasicInformation
		{
			public nuint BaseAddress;
			public nuint AllocationBase;
			public uint AllocationProtect;
			public uint Alignment1;
			public nuint RegionSize;
			public uint State;
			public uint Protect;
			public uint Type;
			public uint Alignment2;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern nuint VirtualQueryEx(SafeProcessHandle process, nint address, out MemoryBasicInformation buffer, nuint length);

		[DllImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, out byte buffer, nuint size, out nuint bytesRead);
	}

	[SupportedOSPlatform("linux")]
	private sealed class LinuxMemory : ProcessMemory
	{
		private readonly SafeFileHandle memory;
		private readonly string mapsPath;

		private LinuxMemory(SafeFileHandle memory, string mapsPath)
		{
			this.memory = memory;
			this.mapsPath = mapsPath;
		}

		public static ProcessMemory? Open(int processId, out string? failure)
		{
			failure = null;
			var mapsPath = $"/proc/{processId}/maps";
			if (!File.Exists(mapsPath)) {
				failure = "The game process is no longer running.";
				return null;
			}

			try {
				var handle = File.OpenHandle($"/proc/{processId}/mem", FileMode.Open, FileAccess.Read);
				return new LinuxMemory(handle, mapsPath);
			} catch (UnauthorizedAccessException) {
				failure = "Access to the game process was denied. Reading another process's memory needs ptrace permission (see /proc/sys/kernel/yama/ptrace_scope).";
			} catch (IOException ex) {
				failure = $"Could not open the game process: {ex.Message}";
			}
			return null;
		}

		public override IEnumerable<MemoryRegion> EnumerateRegions()
		{
			// Read through a stream rather than File.ReadAllLines: /proc reports a zero length,
			// and the map changes under us while the game runs.
			using var reader = new StreamReader(File.OpenRead(mapsPath));
			while (reader.ReadLine() is string line) {
				if (TryParseMapping(line, out var region)) yield return region;
			}
		}

		/// <summary>
		/// Parses one /proc/pid/maps row: "start-end perms offset dev inode pathname". Only
		/// readable private mappings without a backing file are kept, which is where a
		/// Wine-hosted heap lives; the kernel's [vvar] and [vsyscall] rows are not safely
		/// readable at all.
		/// </summary>
		private static bool TryParseMapping(string line, out MemoryRegion region)
		{
			region = default;

			int dash = line.IndexOf('-');
			int permsAt = line.IndexOf(' ');
			if (dash <= 0 || permsAt <= dash) return false;

			if (!ulong.TryParse(line.AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong start)) return false;
			if (!ulong.TryParse(line.AsSpan(dash + 1, permsAt - dash - 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong end)) return false;
			if (end <= start) return false;

			var perms = line.AsSpan(permsAt + 1);
			if (perms.Length < 4 || perms[0] != 'r' || perms[3] != 'p') return false;

			var pathname = Pathname(line, permsAt + 1);
			if (!pathname.IsEmpty && !pathname.SequenceEqual("[heap]") && !pathname.SequenceEqual("[stack]")) return false;

			region = new MemoryRegion(start, end - start);
			return true;
		}

		/// <summary>The row's last field, which is blank for anonymous memory.</summary>
		private static ReadOnlySpan<char> Pathname(string line, int permsAt)
		{
			// perms, offset, dev and inode all sit between the perms field and the pathname.
			int at = permsAt;
			for (int field = 0; field < 4; field++) {
				int space = line.IndexOf(' ', at);
				if (space < 0) return default;

				at = space;
				while (at < line.Length && line[at] == ' ') at++;
			}
			return line.AsSpan(at).Trim();
		}

		public override int Read(ulong address, Span<byte> buffer)
		{
			// Addresses above long.MaxValue cannot be expressed as a file offset, and no
			// user-mode mapping lives there anyway.
			if (buffer.Length == 0 || address > long.MaxValue) return 0;

			try {
				return RandomAccess.Read(memory, buffer, (long)address);
			} catch (Exception) {
				// Unreadable pages surface as IO errors; the caller just moves on.
				return 0;
			}
		}

		public override void Dispose() => memory.Dispose();
	}
}
