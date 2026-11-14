using static RotrTrainer.Core.Register32;

namespace RotrTrainer.Core.Features;

/// <summary>Both calcTimeToBuild functions return one frame for the local player.</summary>
public sealed class InstantBuild : IFeature
{
    private readonly PatchSet _patches = new(CreatePatches);

    public string Name => "Instant build";
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
        // sub esp,8 · push ebx · push esi
        new("ThingTemplate::calcTimeToBuild", addresses.CalcTimeToBuild, [0x83, 0xEC, 0x08, 0x53, 0x56],
            (cave, context) => EmitOneFrameForLocalPlayer(cave, addresses, context)),
        // fld dword [ecx+14h] · fmul dword [30.0f]; the constant's address differs per build
        new("UpgradeTemplate::calcTimeToBuild", addresses.UpgradeCalcTimeToBuild,
            [0xD9, 0x41, 0x14, 0xD8, 0x0D, .. X86.LittleEndian(addresses.UpgradeBuildTimeConstant)],
            (cave, context) => EmitOneFrameForLocalPlayer(cave, addresses, context)),
    ];

    // Both are thiscall Int (Player *player), ret 4, with the player at [esp+4]. The x87 stack stays untouched.
    private static void EmitOneFrameForLocalPlayer(CaveAssembler cave, GameAddresses addresses, CaveContext context)
    {
        CaveGuard.EmitGameChecks(cave, addresses, context, Eax, Edx);
        CaveGuard.EmitLoadLocalPlayer(cave, addresses, context, Edx);
        cave.Emit(X86.CmpWithMemory(Edx, Esp, 4)).JumpIf(Condition.NotEqual, context.FallThrough)
            .Emit(X86.MovImmediate(Eax, 1))
            .Emit(X86.ReturnPop(4));
    }
}
