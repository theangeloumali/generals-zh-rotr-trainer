using static RotrTrainer.Core.Register32;

namespace RotrTrainer.Core.Features;

/// <summary>
/// Skips special-power recharge for the local player's objects: startPowerRecharge (every vtable caller)
/// and its copy inlined in the SpecialPowerModule constructor (objects created already complete).
/// </summary>
public sealed class NoCooldown : IFeature
{
    private readonly PatchSet _patches = new(CreatePatches);

    public string Name => "No cooldown";


    public string Description => "Experimental: no recharge by code patch";
    public bool Enabled { get; set; }
    public bool NeedsArming => true;
    public string? Failure => _patches.Failure;

    public void Apply(GameContext context)
    {
        if (_patches.EnsureInstalled(context.Memory, context.Addresses))
            _patches.Arm(context.Memory, context.Frame);
    }

    public void Remove(IProcessMemory memory) => _patches.Remove(memory);

    internal static IReadOnlyList<CodePatch> CreatePatches(GameAddresses addresses) =>
    [
        // push ebx · push esi · mov esi,ecx · push edi
        new("SpecialPowerModule::startPowerRecharge", addresses.StartPowerRecharge, [0x53, 0x56, 0x8B, 0xF1, 0x57],
            (cave, context) => EmitReturnForLocalOwner(cave, addresses, context)),
        // mov ecx,[TheGameLogic]; the absolute operand differs per build
        new("SpecialPowerModule constructor recharge", addresses.SpecialPowerCtorRechargeSite,
            [0x8B, 0x0D, .. X86.LittleEndian(addresses.GameLogicPointer)],
            (cave, context) => EmitSkipForLocalOwner(cave, addresses, context)),
    ];

    // ecx = the module's interface sub-object and [ecx-8] its Object. getControllingPlayer clobbers eax, ecx and edx.
    private static void EmitReturnForLocalOwner(CaveAssembler cave, GameAddresses addresses, CaveContext context)
    {
        CaveGuard.EmitGameChecks(cave, addresses, context, Eax, Edx);
        cave.Emit(X86.MovFromMemory(Edx, Ecx, -8))
            .Emit(X86.Test(Edx)).JumpIf(Condition.Equal, context.FallThrough)
            .Emit(X86.Push(Ecx))
            .Emit(X86.MovRegister(Ecx, Edx))
            .CallTo(addresses.GetControllingPlayer)
            .Emit(X86.Pop(Ecx));
        CaveGuard.EmitLoadLocalPlayer(cave, addresses, context, Edx);
        cave.Emit(X86.CmpRegisters(Eax, Edx)).JumpIf(Condition.NotEqual, context.FallThrough)
            .Emit(X86.Return()); // no recharge: the power stays ready
    }

    // ebp = the controlling Player. The join leaves m_availableOnFrame at the constructor's 0, so the power is ready.
    private static void EmitSkipForLocalOwner(CaveAssembler cave, GameAddresses addresses, CaveContext context)
    {
        CaveGuard.EmitGameChecks(cave, addresses, context, Eax, Edx);
        CaveGuard.EmitLoadLocalPlayer(cave, addresses, context, Edx);
        cave.Emit(X86.CmpRegisters(Ebp, Edx)).JumpIf(Condition.NotEqual, context.FallThrough)
            .JumpTo(addresses.SpecialPowerCtorRechargeSkip);
    }
}
