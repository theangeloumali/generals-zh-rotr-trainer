using System.Runtime.InteropServices;
using RotrTrainer.Core;
using RotrTrainer.Core.Features;

namespace RotrTrainer;

/// <summary>A feature plus how to read its failure text, which IFeature and FeatureStatus do not carry.</summary>
internal sealed record FeatureBinding(IFeature Feature, Func<string?> Failure);

internal static class Program
{
    private const uint LoadLibrarySearchSystem32 = 0x800;
    private const string ProbeFlag = "--probe";
    // Features that rewrite game code black out the picture on this install; opt-in until the cause is known.
    private const string CodePatchesFlag = "--code-patches";
    private const int ProbeDurationMs = 5000;
    private const int TickIntervalMs = 100;
    private const int ExitOk = 0;
    private const int ExitNothingAttached = 1;
    private const int ExitUsage = 2;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [STAThread]
    private static int Main(string[] args)
    {
        SetDefaultDllDirectories(LoadLibrarySearchSystem32);

        if (args.Length > 0 && args[0] == ProbeFlag)
            return args.Length == 2 ? RunProbe(args[1]) : ExitUsage;

        ApplicationConfiguration.Initialize();
        // The SDK forbids DPI keys in the manifest; set after Initialize, which resets the mode.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        var bindings = CreateBindings(includeCodePatches: args.Contains(CodePatchesFlag));
        using var engine = CreateEngine(bindings);
        Application.Run(new MainForm(engine, bindings));
        return ExitOk;
    }

    // Strictly read-only: no arming, no enabled feature, and no code patches (CrashFix needs no arming).
    private static int RunProbe(string outputPath)
    {
        using var engine = CreateEngine(CreateBindings(includeCodePatches: false));
        for (int elapsedMs = 0; elapsedMs < ProbeDurationMs; elapsedMs += TickIntervalMs)
        {
            engine.Tick();
            Thread.Sleep(TickIntervalMs);
        }

        var status = engine.Status;
        File.WriteAllLines(outputPath,
        [
            $"attached: {status.IsAttached}",
            $"build: {status.BuildName}",
            $"refusal: {status.Refusal}",
            $"armed: {status.IsArmed}",
            $"guard: {status.GuardReason}",
            $"money: {status.Money}",
            $"science_purchase_points: {status.SciencePurchasePoints}",
            $"rank_level: {status.RankLevel}",
        ]);
        return status.IsAttached ? ExitOk : ExitNothingAttached;
    }

    private static TrainerEngine CreateEngine(IReadOnlyList<FeatureBinding> bindings) =>
        new(bindings.Select(binding => binding.Feature).ToList(), AttachToGame, NetworkEndpoints.OwnsAny);

    private static AttachedGame? AttachToGame(out string failure)
    {
        var memory = Win32ProcessMemory.TryAttach(out failure);
        return memory is null ? null : new AttachedGame(memory, memory.ExePath, memory.ProcessId, memory.StartTimeUtc);
    }

    // Order is the display order and the hotkey order from F6; at most five toggles fit F6-F10.
    private static IReadOnlyList<FeatureBinding> CreateBindings(bool includeCodePatches)
    {
        if (!includeCodePatches)
        {
            return
            [
                new(new MoneyFloor(), () => null),
                new(new GeneralPoints(), () => null),
                new(new FastBuild(), () => null),
                new(new FreeBuild(), () => null),
                new(new PowerBonus(), () => null),
            ];
        }

        // Diagnostic set: the code patches alone, to find out why they black out the picture.
        var instantBuild = new InstantBuild();
        var noCooldown = new NoCooldown();
        var unlimitedPower = new UnlimitedPower();
        var crashFix = new CrashFix();
        return
        [
            new(instantBuild, () => instantBuild.Failure),
            new(noCooldown, () => noCooldown.Failure),
            new(unlimitedPower, () => unlimitedPower.Failure),
            new(crashFix, () => crashFix.Failure),
        ];
    }
}
