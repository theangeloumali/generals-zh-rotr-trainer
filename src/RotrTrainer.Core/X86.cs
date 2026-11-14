using System.Buffers.Binary;

namespace RotrTrainer.Core;

public enum Register32
{
    Eax,
    Ecx,
    Edx,
    Ebx,
    Esp,
    Ebp,
    Esi,
    Edi,
}

/// <summary>The low nibble of a Jcc opcode. Below and AboveOrEqual are unsigned compares.</summary>
public enum Condition : byte
{
    Below = 0x2,
    AboveOrEqual = 0x3,
    Equal = 0x4,
    NotEqual = 0x5,
}

/// <summary>Only the x86-32 encodings the caves use. Each one is unit-tested against known bytes.</summary>
public static class X86
{
    public const byte Nop = 0x90;
    public const byte JumpRel32Opcode = 0xE9;
    public const int JumpRel32Length = 5;

    public static byte[] JumpRel32(uint from, uint target) => RelativeBranch(JumpRel32Opcode, from, target);

    public static byte[] CallRel32(uint from, uint target) => RelativeBranch(0xE8, from, target);

    // 0F 8x rel32: six bytes, relative to the end of the instruction.
    public static byte[] JumpIfRel32(Condition condition, uint from, uint target) =>
        [0x0F, (byte)(0x80 | (byte)condition), .. LittleEndian(target - (from + 6))];

    public static byte[] JumpIfShort(Condition condition, sbyte displacement) =>
        [(byte)(0x70 | (byte)condition), (byte)displacement];

    // mov r32,[abs32]
    public static byte[] MovFromAbsolute(Register32 destination, uint address) =>
        [0x8B, ModRm(0, destination, Register32.Ebp), .. LittleEndian(address)];

    // mov r32,[base+displacement]
    public static byte[] MovFromMemory(Register32 destination, Register32 baseRegister, int displacement) =>
        [0x8B, .. MemoryOperand(destination, baseRegister, displacement)];

    public static byte[] MovRegister(Register32 destination, Register32 source) => [0x8B, ModRm(3, destination, source)];

    public static byte[] MovImmediate(Register32 destination, uint value) =>
        [(byte)(0xB8 + (int)destination), .. LittleEndian(value)];

    // mov byte [esp+displacement],value
    public static byte[] MovByteToStack(sbyte displacement, byte value) => [0xC6, 0x44, 0x24, (byte)displacement, value];

    // cmp r32,[abs32]
    public static byte[] CmpWithAbsolute(Register32 left, uint address) =>
        [0x3B, ModRm(0, left, Register32.Ebp), .. LittleEndian(address)];

    // cmp r32,[base+displacement]
    public static byte[] CmpWithMemory(Register32 left, Register32 baseRegister, int displacement) =>
        [0x3B, .. MemoryOperand(left, baseRegister, displacement)];

    public static byte[] CmpRegisters(Register32 left, Register32 right) => [0x3B, ModRm(3, left, right)];

    // cmp r32,imm8 (83 /7, sign-extended)
    public static byte[] CmpImmediate8(Register32 left, sbyte value) => [0x83, ModRm(3, (Register32)7, left), (byte)value];

    // cmp byte [abs32],0 (80 /7)
    public static byte[] CmpByteWithZero(uint address) => [0x80, 0x3D, .. LittleEndian(address), 0x00];

    public static byte[] Test(Register32 register) => [0x85, ModRm(3, register, register)];

    public static byte[] Push(Register32 register) => [(byte)(0x50 + (int)register)];

    public static byte[] Pop(Register32 register) => [(byte)(0x58 + (int)register)];

    public static byte[] Return() => [0xC3];

    public static byte[] ReturnPop(ushort argumentBytes) => [0xC2, (byte)argumentBytes, (byte)(argumentBytes >> 8)];

    // xor al,al: al = 0 and ZF = 1
    public static byte[] ClearAl() => [0x32, 0xC0];

    // fld1: pushes 1.0 onto the x87 stack
    public static byte[] LoadOne() => [0xD9, 0xE8];

    public static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] RelativeBranch(byte opcode, uint from, uint target) =>
        [opcode, .. LittleEndian(target - (from + JumpRel32Length))];

    private static byte ModRm(int mode, Register32 register, Register32 rm) =>
        (byte)((mode << 6) | ((int)register << 3) | (int)rm);

    // esp as a base register needs a SIB byte (24h = no index, base esp).
    private static byte[] MemoryOperand(Register32 register, Register32 baseRegister, int displacement)
    {
        bool fitsInByte = displacement is >= sbyte.MinValue and <= sbyte.MaxValue;
        byte[] sib = baseRegister == Register32.Esp ? [0x24] : [];
        byte[] displacementBytes = fitsInByte ? [(byte)(sbyte)displacement] : LittleEndian((uint)displacement);
        return [ModRm(fitsInByte ? 1 : 2, register, baseRegister), .. sib, .. displacementBytes];
    }
}

/// <summary>A forward branch target inside one cave.</summary>
public sealed class CaveLabel
{
    internal List<int> PendingBranchEnds { get; } = [];

    public int? Offset { get; internal set; }
}

/// <summary>Assembles one cave at a known address. Branches to labels are short and forward only.</summary>
public sealed class CaveAssembler(uint origin)
{
    private readonly List<byte> _code = [];
    private readonly HashSet<CaveLabel> _openLabels = [];

    public uint Here => origin + (uint)_code.Count;

    public CaveAssembler Emit(ReadOnlySpan<byte> bytes)
    {
        _code.AddRange(bytes);
        return this;
    }

    public CaveAssembler JumpIf(Condition condition, CaveLabel label)
    {
        if (label.Offset is not null)
            throw new InvalidOperationException("cave branches only go forward");
        Emit(X86.JumpIfShort(condition, 0));
        label.PendingBranchEnds.Add(_code.Count);
        _openLabels.Add(label);
        return this;
    }

    public CaveAssembler Place(CaveLabel label)
    {
        foreach (int branchEnd in label.PendingBranchEnds)
        {
            int displacement = _code.Count - branchEnd;
            if (displacement > sbyte.MaxValue)
                throw new InvalidOperationException($"a short branch of {displacement} bytes does not fit in rel8");
            _code[branchEnd - 1] = (byte)displacement;
        }
        label.PendingBranchEnds.Clear();
        label.Offset = _code.Count;
        _openLabels.Remove(label);
        return this;
    }

    public CaveAssembler JumpTo(uint target) => Emit(X86.JumpRel32(Here, target));

    public CaveAssembler JumpIfTo(Condition condition, uint target) => Emit(X86.JumpIfRel32(condition, Here, target));

    public CaveAssembler CallTo(uint target) => Emit(X86.CallRel32(Here, target));

    public byte[] ToArray()
    {
        if (_openLabels.Count > 0)
            throw new InvalidOperationException("a branch targets a label that was never placed");
        return [.. _code];
    }
}
