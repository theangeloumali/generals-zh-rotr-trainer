using System.Buffers.Binary;
using static RotrTrainer.Core.NativeMethods;

namespace RotrTrainer.Core;

/// <summary>Local, read-only check of the OS socket tables for endpoints owned by a pid.</summary>
public static class NetworkEndpoints
{
    private delegate int TableQuery(byte[]? table, ref int size, bool order, int addressFamily, int tableClass, int reserved);

    // (row size, pid offset) of MIB_TCPROW_OWNER_PID, MIB_TCP6ROW_OWNER_PID, MIB_UDPROW_OWNER_PID, MIB_UDP6ROW_OWNER_PID.
    private static readonly (TableQuery Query, int Family, int TableClass, int RowSize, int PidOffset)[] Tables =
    [
        (GetExtendedTcpTable, AfInet, TcpTableOwnerPidAll, 24, 20),
        (GetExtendedTcpTable, AfInet6, TcpTableOwnerPidAll, 56, 52),
        (GetExtendedUdpTable, AfInet, UdpTableOwnerPid, 12, 8),
        (GetExtendedUdpTable, AfInet6, UdpTableOwnerPid, 28, 24),
    ];

    /// <summary>True when the pid owns any TCP or UDP endpoint; null when a table query failed.</summary>
    public static bool? OwnsAny(int processId)
    {
        foreach (var table in Tables)
        {
            byte[]? rows = ReadTable(table.Query, table.Family, table.TableClass);
            if (rows is null)
                return null;
            if (ContainsPid(rows, table.RowSize, table.PidOffset, processId))
                return true;
        }
        return false;
    }

    private static byte[]? ReadTable(TableQuery query, int family, int tableClass)
    {
        int size = 0;
        if (query(null, ref size, false, family, tableClass, 0) != ErrorInsufficientBuffer)
            return null;
        // The table can grow between the size probe and the read, so retry a few times.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            var buffer = new byte[size];
            int result = query(buffer, ref size, false, family, tableClass, 0);
            if (result == 0)
                return buffer;
            if (result != ErrorInsufficientBuffer)
                return null;
        }
        return null;
    }

    private static bool ContainsPid(ReadOnlySpan<byte> table, int rowSize, int pidOffset, int processId)
    {
        if (table.Length < 4)
            return false;
        long rows = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(table), (table.Length - 4) / rowSize);
        for (int row = 0; row < rows; row++)
            if (BinaryPrimitives.ReadInt32LittleEndian(table[(4 + row * rowSize + pidOffset)..]) == processId)
                return true;
        return false;
    }
}
