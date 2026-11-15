using static RotrTrainer.Core.Register32;

namespace RotrTrainer.Core.Features;

/// <summary>
/// Early return for a NULL unit in AIUpdateInterface::privateMoveAwayFromUnit (docs/anchors.md).
/// It has no gameplay effect, so it carries no guard and stays installed while a known build is attached.
/// </summary>
public sealed class CrashFix : IFeature
{
    private readonly PatchSet _patches = new(CreatePatches);

    public string Name => "Move-away crash fix";


    public string Description => "Experimental: fixes a known engine crash";
    public bool Enabled { get; set; } = true;
    public bool NeedsArming => false;
    public string? Failure => _patches.Failure;

    public void Apply(GameContext context) => _patches.EnsureInstalled(context.Memory, context.Addresses);

    public void Remove(IProcessMemory memory) => _patches.Remove(memory);

    internal static IReadOnlyList<CodePatch> CreatePatches(GameAddresses addresses) =>
    [
        // mov edx,[esp+18h] (unit) · mov ecx,[edx+64h], the load that faults when unit is NULL
        new("AIUpdateInterface::privateMoveAwayFromUnit", addresses.MoveAwayFromUnitNullSite,
            [0x8B, 0x54, 0x24, 0x18, 0x8B, 0x4A, 0x64],
            (cave, context) => cave
                .Emit(X86.MovFromMemory(Edx, Esp, 0x18))
                .Emit(X86.Test(Edx))
                .JumpIfTo(Condition.Equal, addresses.MoveAwayFromUnitExit) // same stack depth as the exit's pops
                .Emit(X86.Test(Eax))), // re-creates the ZF the je after the span reads; the stolen movs keep it
    ];
}
