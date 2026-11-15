using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// An elevated process must never pick up a native DLL from its own folder.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace RotrTrainer.Core;

internal static class NativeMethods
{
    public const uint ProcessVmOperation = 0x0008;
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessVmWrite = 0x0020;
    public const uint ProcessSuspendResume = 0x0800;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint Synchronize = 0x00100000;

    public const uint MemCommitReserve = 0x3000;
    public const uint PageReadWrite = 0x04;
    public const uint PageExecuteRead = 0x20;

    public const uint WaitTimeout = 0x102;
    public const int ErrorInsufficientBuffer = 122;

    public const int AfInet = 2;
    public const int AfInet6 = 23;
    public const int TcpTableOwnerPidAll = 5;
    public const int UdpTableOwnerPid = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(SafeProcessHandle process, nint baseAddress, ref byte buffer, nint size, out nint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(SafeProcessHandle process, nint baseAddress, ref byte buffer, nint size, out nint bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint VirtualAllocEx(SafeProcessHandle process, nint address, nint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualProtectEx(SafeProcessHandle process, nint address, nint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushInstructionCache(SafeProcessHandle process, nint address, nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char[] exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(SafeProcessHandle process);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(SafeProcessHandle process);

    [DllImport("iphlpapi.dll")]
    public static extern int GetExtendedTcpTable(byte[]? table, ref int size, bool order, int addressFamily, int tableClass, int reserved);

    [DllImport("iphlpapi.dll")]
    public static extern int GetExtendedUdpTable(byte[]? table, ref int size, bool order, int addressFamily, int tableClass, int reserved);
}
