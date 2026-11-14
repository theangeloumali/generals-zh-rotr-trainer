using System.Buffers.Binary;
using RotrTrainer.Core;

namespace RotrTrainer.Tests;

public sealed record FakeWrite(uint Address, byte[] Data, bool WhileSuspended);

public sealed record FakeProtection(uint Address, int Size, PageAccess Access);

/// <summary>Sparse in-memory process: unmapped bytes fail reads and writes, like a real address space.</summary>
public sealed class FakeMemory : IProcessMemory
{
    private readonly Dictionary<uint, byte> _bytes = [];
    private uint _nextAllocation = 0x30000000;

    public bool IsAlive { get; set; } = true;
    public bool FailReads { get; set; }
    public bool FailWrites { get; set; }
    public bool FailAllocations { get; set; }

    public List<FakeWrite> Writes { get; } = [];
    public List<FakeProtection> ProtectionChanges { get; } = [];
    public List<(uint Address, int Size)> Flushes { get; } = [];
    public int SuspendCount { get; private set; }
    public int ResumeCount { get; private set; }
    public bool IsSuspended => SuspendCount > ResumeCount;

    public void Map(uint address, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
            _bytes[address + (uint)i] = data[i];
    }

    public void MapUInt32(uint address, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        Map(address, buffer);
    }

    public byte[] Peek(uint address, int count)
    {
        var result = new byte[count];
        for (int i = 0; i < count; i++)
            result[i] = _bytes[address + (uint)i];
        return result;
    }

    public bool TryRead(uint address, Span<byte> buffer)
    {
        if (FailReads || !IsAlive || !IsMapped(address, buffer.Length))
            return false;
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = _bytes[address + (uint)i];
        return true;
    }

    public bool TryWrite(uint address, ReadOnlySpan<byte> data)
    {
        if (FailWrites || !IsAlive || !IsMapped(address, data.Length))
            return false;
        Map(address, data);
        Writes.Add(new FakeWrite(address, data.ToArray(), IsSuspended));
        return true;
    }

    public uint Allocate(int size)
    {
        if (FailAllocations || !IsAlive || size <= 0)
            return 0;
        uint address = _nextAllocation;
        Map(address, new byte[size]);
        _nextAllocation += (uint)((size + 0xFFF) & ~0xFFF);
        return address;
    }

    public bool Protect(uint address, int size, PageAccess access)
    {
        if (!IsAlive)
            return false;
        ProtectionChanges.Add(new FakeProtection(address, size, access));
        return true;
    }

    public void SuspendThreads() => SuspendCount++;

    public void ResumeThreads() => ResumeCount++;

    public bool FlushCode(uint address, int size)
    {
        if (!IsAlive)
            return false;
        Flushes.Add((address, size));
        return true;
    }

    private bool IsMapped(uint address, int length)
    {
        for (int i = 0; i < length; i++)
            if (!_bytes.ContainsKey(address + (uint)i))
                return false;
        return true;
    }
}
