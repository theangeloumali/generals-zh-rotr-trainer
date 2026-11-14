using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RotrTrainer.Core;

public sealed record PeSection(string Name, uint VirtualAddress, uint VirtualSize, uint RawSize, uint RawPointer)
{
    // modded.exe's .text VirtualSize exceeds its raw size; only bytes present in both are real code.
    public uint MappedSize => VirtualSize == 0 ? RawSize : Math.Min(VirtualSize, RawSize);
}

/// <summary>A 32-bit PE file parsed from bytes; sections must lie fully inside the file.</summary>
public sealed class PeImage
{
    private const ushort MachineI386 = 0x14C;
    private const ushort OptionalMagicPe32 = 0x10B;
    private const int SectionHeaderSize = 40;

    private readonly byte[] _file;
    private readonly PeSection _text;

    private PeImage(byte[] file, uint imageBase, uint fileAlignment, IReadOnlyList<PeSection> sections, PeSection text)
    {
        _file = file;
        ImageBase = imageBase;
        FileAlignment = fileAlignment;
        Sections = sections;
        _text = text;
    }

    public uint ImageBase { get; }
    public uint FileAlignment { get; }
    public IReadOnlyList<PeSection> Sections { get; }
    public uint TextVa => ImageBase + _text.VirtualAddress;
    public ReadOnlySpan<byte> Text => _file.AsSpan((int)_text.RawPointer, (int)_text.MappedSize);

    public static bool TryParse(byte[] file, [NotNullWhen(true)] out PeImage? image)
    {
        image = null;
        if (file.Length < 0x40 || file[0] != 'M' || file[1] != 'Z')
            return false;

        int peHeader = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C));
        if (peHeader < 0x40 || peHeader > file.Length - 24 - 0x60)
            return false;
        var span = file.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[peHeader..]) != 0x00004550
            || BinaryPrimitives.ReadUInt16LittleEndian(span[(peHeader + 4)..]) != MachineI386)
            return false;

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(span[(peHeader + 6)..]);
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(span[(peHeader + 20)..]);
        int optional = peHeader + 24;
        if (BinaryPrimitives.ReadUInt16LittleEndian(span[optional..]) != OptionalMagicPe32)
            return false;
        uint imageBase = BinaryPrimitives.ReadUInt32LittleEndian(span[(optional + 28)..]);
        uint fileAlignment = BinaryPrimitives.ReadUInt32LittleEndian(span[(optional + 36)..]);

        int table = optional + optionalSize;
        if (sectionCount == 0 || (long)table + (long)sectionCount * SectionHeaderSize > file.Length)
            return false;

        var sections = new List<PeSection>(sectionCount);
        for (int i = 0; i < sectionCount; i++)
        {
            var header = span.Slice(table + i * SectionHeaderSize, SectionHeaderSize);
            var section = new PeSection(
                Encoding.ASCII.GetString(header[..8]).TrimEnd('\0'),
                VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
                VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(header[8..]),
                RawSize: BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
                RawPointer: BinaryPrimitives.ReadUInt32LittleEndian(header[20..]));
            if ((ulong)section.RawPointer + section.RawSize > (ulong)file.Length)
                return false;
            sections.Add(section);
        }

        var text = sections.Find(section => section.Name == ".text");
        if (text is null || text.MappedSize == 0)
            return false;
        image = new PeImage(file, imageBase, fileAlignment, sections, text);
        return true;
    }

    public uint? VaToFileOffset(uint va)
    {
        uint rva = va - ImageBase;
        foreach (var section in Sections)
            if (va >= ImageBase && rva >= section.VirtualAddress && rva - section.VirtualAddress < section.MappedSize)
                return section.RawPointer + (rva - section.VirtualAddress);
        return null;
    }

    public uint? FileOffsetToVa(uint offset)
    {
        foreach (var section in Sections)
            if (offset >= section.RawPointer && offset - section.RawPointer < section.MappedSize)
                return ImageBase + section.VirtualAddress + (offset - section.RawPointer);
        return null;
    }

    /// <summary>True when the VA lands in any section once loaded, including zero-filled tails.</summary>
    public bool ContainsVa(uint va)
    {
        uint rva = va - ImageBase;
        foreach (var section in Sections)
            if (va >= ImageBase && rva >= section.VirtualAddress
                && rva - section.VirtualAddress < Math.Max(section.VirtualSize, section.RawSize))
                return true;
        return false;
    }
}
