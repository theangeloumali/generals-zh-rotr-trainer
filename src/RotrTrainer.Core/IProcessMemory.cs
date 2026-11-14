namespace RotrTrainer.Core;

public enum PageAccess
{
    ReadWrite,
    ExecuteRead,
}

/// <summary>The only door into the game process; tests swap in a fake.</summary>
public interface IProcessMemory
{
    bool IsAlive { get; }

    /// <summary>False on any failure; the buffer is then undefined.</summary>
    bool TryRead(uint address, Span<byte> buffer);

    bool TryWrite(uint address, ReadOnlySpan<byte> data);

    /// <summary>Returns 0 on failure. New pages start ReadWrite.</summary>
    uint Allocate(int size);

    bool Protect(uint address, int size, PageAccess access);

    void SuspendThreads();

    void ResumeThreads();

    bool FlushCode(uint address, int size);
}

public static class ProcessMemoryExtensions
{
    public static uint? ReadUInt32(this IProcessMemory memory, uint address)
    {
        Span<byte> buffer = stackalloc byte[4];
        return memory.TryRead(address, buffer) ? BitConverter.ToUInt32(buffer) : null;
    }

    public static bool WriteUInt32(this IProcessMemory memory, uint address, uint value) =>
        memory.TryWrite(address, BitConverter.GetBytes(value));
}
