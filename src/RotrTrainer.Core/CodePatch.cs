using System.Buffers.Binary;

namespace RotrTrainer.Core;

public sealed record PatchResult(bool Succeeded, string Message);

/// <summary>The writable block a guarded cave reads. It sits on its own ReadWrite page just below the cave.</summary>
public sealed record GuardBlock(uint Address)
{
    public const int Size = 12;

    public uint Armed => Address;

    // A new match restarts at frame 0, so a window left by a dead trainer cannot reopen there.
    public uint StartFrame => Address + 4;

    public uint ExpiryFrame => Address + 8;
}

/// <summary>What a cave body may branch to.</summary>
public sealed record CaveContext(GuardBlock Guard, CaveLabel FallThrough, uint ReturnAddress);

/// <summary>Writes a cave's own logic. <see cref="CodePatch"/> then appends the fall-through: the original bytes and a jump back.</summary>
public delegate void CaveBody(CaveAssembler cave, CaveContext context);

public sealed record Cave(byte[] Code, int FallThroughOffset);

/// <summary>
/// One jump-to-cave patch over a whole-instruction span. Every site write happens with the game suspended
/// and is read back; any failure leaves the original bytes in place.
/// </summary>
public sealed class CodePatch
{
    /// <summary>About two seconds at the engine's 30 logic frames per second.</summary>
    public const uint DeadManFrames = 60;

    internal const int PageSize = 0x1000;

    private readonly byte[] _originalBytes;
    private readonly CaveBody _body;

    // Guard page, then cave page. Never freed: a game thread may still be inside the cave after unhooking.
    private uint _allocation;
    private bool _isCaveWritten;

    public CodePatch(string name, uint site, byte[] originalBytes, CaveBody body)
    {
        if (originalBytes.Length < X86.JumpRel32Length)
            throw new ArgumentException($"{name}: the span must hold a 5-byte jump", nameof(originalBytes));
        Name = name;
        Site = site;
        _originalBytes = originalBytes;
        _body = body;
    }

    public string Name { get; }
    public uint Site { get; }
    public ReadOnlySpan<byte> OriginalBytes => _originalBytes;
    public uint ReturnAddress => Site + (uint)_originalBytes.Length;
    public bool IsInstalled { get; private set; }
    public uint CaveAddress => _allocation == 0 ? 0 : _allocation + PageSize;

    internal Cave BuildCave(uint caveAddress, GuardBlock guard)
    {
        var cave = new CaveAssembler(caveAddress);
        var fallThrough = new CaveLabel();
        _body(cave, new CaveContext(guard, fallThrough, ReturnAddress));
        cave.Place(fallThrough).Emit(_originalBytes).JumpTo(ReturnAddress);
        return new Cave(cave.ToArray(), fallThrough.Offset!.Value);
    }

    public PatchResult Install(IProcessMemory memory)
    {
        if (IsInstalled)
            return Succeeded();
        byte[] live = new byte[_originalBytes.Length];
        if (!memory.TryRead(Site, live))
            return Failed("cannot read the site");
        if (!live.AsSpan().SequenceEqual(_originalBytes))
        {
            // A hook already there is not ours to remove: it may belong to another tool, or to a trainer
            // that died in this game process. Either way the safe answer is to leave the game's code alone.
            return Failed(IsJumpPatch(live)
                ? "something else has already hooked this site; restart the game"
                : $"site holds {Convert.ToHexString(live)}, not the expected {Convert.ToHexString(_originalBytes)}");
        }
        if (!WriteCave(memory))
            return Failed("could not allocate, write or protect the cave");
        var hooked = WriteSite(memory, JumpToCave(), restoreOnFailure: true);
        IsInstalled = hooked.Succeeded;
        return hooked;
    }

    /// <summary>Restores the original bytes. Safe to call twice, or when nothing was installed.</summary>
    public PatchResult Uninstall(IProcessMemory memory)
    {
        // Disarm first, so the cave falls through even if the restore below fails.
        if (_allocation != 0)
            memory.TryWrite(_allocation, new byte[1]);
        byte[] live = new byte[_originalBytes.Length];
        if (!memory.TryRead(Site, live))
            return Failed("cannot read the site");
        if (live.AsSpan().SequenceEqual(_originalBytes))
        {
            IsInstalled = false;
            return Succeeded();
        }
        // Only our own jump is ours to undo; anything else at the site is left untouched.
        if (!IsInstalled || !live.AsSpan().SequenceEqual(JumpToCave()))
            return IsInstalled ? Failed($"site holds {Convert.ToHexString(live)}; left untouched") : Succeeded();
        var restored = WriteSite(memory, _originalBytes, restoreOnFailure: false);
        if (restored.Succeeded)
            IsInstalled = false;
        return restored;
    }

    /// <summary>Lets the cave act for frames in [currentFrame, currentFrame + DeadManFrames). Called every tick.</summary>
    public bool Arm(IProcessMemory memory, uint currentFrame)
    {
        if (_allocation == 0)
            return false;
        Span<byte> block = stackalloc byte[GuardBlock.Size];
        block.Clear();
        block[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], currentFrame);
        BinaryPrimitives.WriteUInt32LittleEndian(block[8..], currentFrame + DeadManFrames);
        return memory.TryWrite(_allocation, block);
    }

    private bool WriteCave(IProcessMemory memory)
    {
        if (_isCaveWritten)
            return true;
        if (_allocation == 0)
            _allocation = memory.Allocate(2 * PageSize);
        if (_allocation == 0)
            return false;
        byte[] code = BuildCave(CaveAddress, new GuardBlock(_allocation)).Code;
        _isCaveWritten = code.Length <= PageSize
            && memory.TryWrite(CaveAddress, code)
            && memory.Protect(CaveAddress, PageSize, PageAccess.ExecuteRead);
        return _isCaveWritten;
    }

    private PatchResult WriteSite(IProcessMemory memory, byte[] bytes, bool restoreOnFailure)
    {
        try
        {
            memory.SuspendThreads();
        }
        catch (InvalidOperationException error)
        {
            return Failed($"could not suspend the game: {error.Message}");
        }

        string? failure = null;
        try
        {
            if (!WriteAndVerify(memory, bytes))
            {
                failure = "write, read-back or flush failed";
                if (restoreOnFailure && !WriteAndVerify(memory, _originalBytes))
                    failure += "; restoring the original bytes also failed";
            }
        }
        finally
        {
            try
            {
                memory.ResumeThreads();
            }
            catch (InvalidOperationException error)
            {
                failure = $"{failure ?? "bytes written"}; resuming the game failed: {error.Message}";
            }
        }
        return failure is null ? Succeeded() : Failed(failure);
    }

    private bool WriteAndVerify(IProcessMemory memory, byte[] bytes)
    {
        byte[] readBack = new byte[bytes.Length];
        return memory.TryWrite(Site, bytes)
            && memory.TryRead(Site, readBack)
            && readBack.AsSpan().SequenceEqual(bytes)
            && memory.FlushCode(Site, bytes.Length);
    }

    private byte[] JumpToCave()
    {
        byte[] jump = new byte[_originalBytes.Length];
        Array.Fill(jump, X86.Nop);
        X86.JumpRel32(Site, CaveAddress).CopyTo(jump, 0);
        return jump;
    }

    private static bool IsJumpPatch(ReadOnlySpan<byte> live) =>
        live[0] == X86.JumpRel32Opcode && !live[X86.JumpRel32Length..].ContainsAnyExcept(X86.Nop);

    private static PatchResult Succeeded() => new(true, "ok");

    private PatchResult Failed(string reason) => new(false, $"{Name} at 0x{Site:X8}: {reason}");
}

/// <summary>A feature's patches for one process and build, installed all-or-nothing.</summary>
public sealed class PatchSet(Func<GameAddresses, IReadOnlyList<CodePatch>> createPatches)
{
    private IReadOnlyList<CodePatch> _patches = [];
    private IProcessMemory? _memory;
    private GameAddresses? _addresses;

    /// <summary>Why the last install or removal failed. A failed install is not retried until Remove, so a bad site never suspends the game every tick.</summary>
    public string? Failure { get; private set; }

    public bool EnsureInstalled(IProcessMemory memory, GameAddresses addresses)
    {
        if (!ReferenceEquals(memory, _memory) || addresses != _addresses)
        {
            _patches = createPatches(addresses);
            _memory = memory;
            _addresses = addresses;
            Failure = null;
        }
        if (Failure is not null)
            return false;
        foreach (var patch in _patches)
        {
            var result = patch.Install(memory);
            if (!result.Succeeded)
            {
                UninstallAll(memory);
                Failure = result.Message;
                return false;
            }
        }
        return true;
    }

    public void Arm(IProcessMemory memory, uint currentFrame)
    {
        foreach (var patch in _patches)
            patch.Arm(memory, currentFrame);
    }

    public void Remove(IProcessMemory memory) => Failure = UninstallAll(memory);

    private string? UninstallAll(IProcessMemory memory)
    {
        string? firstFailure = null;
        foreach (var patch in _patches)
        {
            var result = patch.Uninstall(memory);
            firstFailure ??= result.Succeeded ? null : result.Message;
        }
        return firstFailure;
    }
}

/// <summary>The in-cave guard (security finding 1). Every failed check branches to the fall-through block.</summary>
public static class CaveGuard
{
    /// <summary>A game in progress, single player or skirmish, armed, inside the frame window. Clobbers both registers.</summary>
    public static void EmitGameChecks(CaveAssembler cave, GameAddresses addresses, CaveContext context, Register32 gameLogic, Register32 scratch)
    {
        var fallThrough = context.FallThrough;
        var modeAllowed = new CaveLabel();
        cave.Emit(X86.MovFromAbsolute(gameLogic, addresses.GameLogicPointer))
            .Emit(X86.Test(gameLogic)).JumpIf(Condition.Equal, fallThrough)
            .Emit(X86.MovFromMemory(scratch, gameLogic, (int)GameAddresses.GameLogicGameMode))
            .Emit(X86.Test(scratch)).JumpIf(Condition.Equal, modeAllowed)
            .Emit(X86.CmpImmediate8(scratch, (sbyte)GameMode.Skirmish)).JumpIf(Condition.NotEqual, fallThrough)
            .Place(modeAllowed)
            .Emit(X86.CmpByteWithZero(context.Guard.Armed)).JumpIf(Condition.Equal, fallThrough)
            .Emit(X86.MovFromMemory(scratch, gameLogic, (int)GameAddresses.GameLogicFrame))
            .Emit(X86.CmpWithAbsolute(scratch, context.Guard.StartFrame)).JumpIf(Condition.Below, fallThrough)
            .Emit(X86.CmpWithAbsolute(scratch, context.Guard.ExpiryFrame)).JumpIf(Condition.AboveOrEqual, fallThrough);
    }

    /// <summary>Reads m_local from ThePlayerList at call time, never a baked pointer. NULL falls through.</summary>
    public static void EmitLoadLocalPlayer(CaveAssembler cave, GameAddresses addresses, CaveContext context, Register32 localPlayer)
    {
        cave.Emit(X86.MovFromAbsolute(localPlayer, addresses.PlayerListPointer))
            .Emit(X86.Test(localPlayer)).JumpIf(Condition.Equal, context.FallThrough)
            .Emit(X86.MovFromMemory(localPlayer, localPlayer, (int)GameAddresses.PlayerListLocalPlayer))
            .Emit(X86.Test(localPlayer)).JumpIf(Condition.Equal, context.FallThrough);
    }
}
