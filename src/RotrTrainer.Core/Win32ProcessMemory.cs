using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static RotrTrainer.Core.NativeMethods;

namespace RotrTrainer.Core;

/// <summary>
/// IProcessMemory over a handle with the minimum rights the trainer needs; no thread creation, no RWX pages.
/// The handle pins the original process object, so a reused pid can never redirect reads.
/// </summary>
public sealed class Win32ProcessMemory : IProcessMemory, IDisposable
{
    private const uint ValueAccessRights = ProcessVmRead | ProcessVmWrite | ProcessVmOperation
        | ProcessQueryLimitedInformation | Synchronize;

    private static readonly string[] GameProcessNames = ["modded", "game.dat"];

    private readonly SafeProcessHandle _handle;

    private Win32ProcessMemory(SafeProcessHandle handle, int processId, string exePath, DateTime startTimeUtc)
    {
        _handle = handle;
        ProcessId = processId;
        ExePath = exePath;
        StartTimeUtc = startTimeUtc;
    }

    public int ProcessId { get; }
    public string ExePath { get; }

    /// <summary>Compare across re-attaches: same pid with a different start time is a restarted game.</summary>
    public DateTime StartTimeUtc { get; }

    public bool IsAlive => !_handle.IsClosed && WaitForSingleObject(_handle, 0) == WaitTimeout;

    /// <param name="allowSuspend">Only code patches pause the game; value features never ask for that right.</param>
    public static Win32ProcessMemory? TryAttach(bool allowSuspend, out string failure)
    {
        var candidates = new List<int>();
        foreach (string name in GameProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                candidates.Add(process.Id);
                process.Dispose();
            }
        }

        switch (candidates.Count)
        {
            case 0:
                failure = "game is not running (no modded or game.dat process)";
                return null;
            case > 1:
                failure = $"{candidates.Count} game processes are running; close all but one";
                return null;
            default:
                return Open(candidates[0], allowSuspend, out failure);
        }
    }

    public static Win32ProcessMemory? Open(int processId, bool allowSuspend, out string failure)
    {
        uint accessRights = allowSuspend ? ValueAccessRights | ProcessSuspendResume : ValueAccessRights;
        var handle = OpenProcess(accessRights, false, processId);
        if (handle.IsInvalid)
        {
            failure = $"cannot open process {processId}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            handle.Dispose();
            return null;
        }

        var path = new char[1024];
        int length = path.Length;
        if (!QueryFullProcessImageNameW(handle, 0, path, ref length)
            || !GetProcessTimes(handle, out long creation, out _, out _, out _))
        {
            failure = $"cannot query process {processId}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            handle.Dispose();
            return null;
        }

        failure = "";
        return new Win32ProcessMemory(handle, processId, new string(path, 0, length), DateTime.FromFileTimeUtc(creation));
    }

    public bool TryRead(uint address, Span<byte> buffer) =>
        buffer.IsEmpty
        || (!_handle.IsClosed
            && ReadProcessMemory(_handle, (nint)address, ref MemoryMarshal.GetReference(buffer), buffer.Length, out nint read)
            && read == buffer.Length);

    public bool TryWrite(uint address, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return true;
        // WriteProcessMemory only reads from the buffer; a copy keeps the ReadOnlySpan honest.
        byte[] copy = data.ToArray();
        return !_handle.IsClosed
            && WriteProcessMemory(_handle, (nint)address, ref copy[0], copy.Length, out nint written)
            && written == copy.Length;
    }

    public uint Allocate(int size)
    {
        if (size <= 0 || _handle.IsClosed)
            return 0;
        nint address = VirtualAllocEx(_handle, 0, size, MemCommitReserve, PageReadWrite);
        // A 32-bit target never gets pages above 4 GB; anything else is not addressable by the contract.
        return address > 0 && address <= uint.MaxValue ? (uint)address : 0;
    }

    public bool Protect(uint address, int size, PageAccess access)
    {
        uint protection = access == PageAccess.ExecuteRead ? PageExecuteRead : PageReadWrite;
        return !_handle.IsClosed && VirtualProtectEx(_handle, (nint)address, size, protection, out _);
    }

    public void SuspendThreads()
    {
        int status = NtSuspendProcess(_handle);
        if (status != 0)
            throw new InvalidOperationException($"NtSuspendProcess failed (NTSTATUS 0x{status:X8})");
    }

    public void ResumeThreads()
    {
        int status = NtResumeProcess(_handle);
        if (status != 0)
            throw new InvalidOperationException($"NtResumeProcess failed (NTSTATUS 0x{status:X8})");
    }

    public bool FlushCode(uint address, int size) =>
        !_handle.IsClosed && FlushInstructionCache(_handle, (nint)address, size);

    public void Dispose() => _handle.Dispose();
}
