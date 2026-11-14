using static RotrTrainer.Core.Register32;

namespace RotrTrainer.Core.Features;

/// <summary>
/// Patch-only power (no production bonus: it broke across restart and save). The local player never browns
/// out, builds at full speed in a deficit, and a sabotaged plant stops zeroing its production.
/// </summary>
public sealed class UnlimitedPower : IFeature
{
    private readonly PatchSet _patches = new(CreatePatches);

    public string Name => "Unlimited power";
    public bool Enabled { get; set; }
    public bool NeedsArming => true;
    public string? Failure => _patches.Failure;

    public void Apply(GameContext context)
    {
        if (!_patches.EnsureInstalled(context.Memory, context.Addresses))
            return;
        _patches.Arm(context.Memory, context.Frame);
        ClearSabotage(context);
    }

    public void Remove(IProcessMemory memory) => _patches.Remove(memory);

    internal static IReadOnlyList<CodePatch> CreatePatches(GameAddresses addresses) =>
    [
        // mov al,[esp+4] · test al,al; the je at +0Ah reads ZF from this test, so it must run last
        new("Player::onPowerBrownOutChange", addresses.OnPowerBrownOutChange, [0x8A, 0x44, 0x24, 0x04, 0x84, 0xC0],
            (cave, context) =>
            {
                CaveGuard.EmitGameChecks(cave, addresses, context, Eax, Edx);
                CaveGuard.EmitLoadLocalPlayer(cave, addresses, context, Edx);
                cave.Emit(X86.CmpRegisters(Ecx, Edx)).JumpIf(Condition.NotEqual, context.FallThrough)
                    .Emit(X86.MovByteToStack(4, 0)); // brownOut = FALSE, then the original instructions
            }),
        // neg al · sbb al,al · inc al: al = !hasSufficientPower, ZF = (al == 0). eax must survive the guard.
        new("Player::update brown-out", addresses.PlayerUpdateBrownOutSite, [0xF6, 0xD8, 0x1A, 0xC0, 0xFE, 0xC0],
            (cave, context) =>
            {
                CaveGuard.EmitGameChecks(cave, addresses, context, Ecx, Edx);
                CaveGuard.EmitLoadLocalPlayer(cave, addresses, context, Edx);
                cave.Emit(X86.CmpRegisters(Ebp, Edx)).JumpIf(Condition.NotEqual, context.FallThrough)
                    .Emit(X86.ClearAl()) // the same al and ZF as having enough power
                    .JumpTo(context.ReturnAddress);
            }),
        // push ecx · mov eax,[TheGameLogic]; the absolute operand differs per build
        new("Energy::getEnergySupplyRatio", addresses.GetEnergySupplyRatio, [0x51, 0xA1, .. X86.LittleEndian(addresses.GameLogicPointer)],
            (cave, context) =>
            {
                CaveGuard.EmitGameChecks(cave, addresses, context, Eax, Edx);
                CaveGuard.EmitLoadLocalPlayer(cave, addresses, context, Edx);
                cave.Emit(X86.CmpWithMemory(Edx, Ecx, (int)GameAddresses.EnergyOwnerPlayer)).JumpIf(Condition.NotEqual, context.FallThrough)
                    .Emit(X86.LoadOne()) // ratio 1.0: no build-speed penalty in a deficit
                    .Emit(X86.Return());
            }),
    ];

    // While Player+8Ch is ahead of the current frame, getProduction reports zero.
    private static void ClearSabotage(GameContext context)
    {
        if (context.LocalPlayer == 0)
            return;
        uint sabotageField = context.LocalPlayer + GameAddresses.PlayerEnergySabotageFrame;
        if (context.Memory.ReadUInt32(sabotageField) is uint sabotagedUntil && sabotagedUntil != 0)
            context.Memory.WriteUInt32(sabotageField, 0);
    }
}
