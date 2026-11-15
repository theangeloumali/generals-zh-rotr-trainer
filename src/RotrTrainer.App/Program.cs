using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using RotrTrainer.Core;
using RotrTrainer.Core.Features;

// An elevated process must never pick up a native DLL from its own folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace RotrTrainer;

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
    private const int ExitUnsafeFolder = 3;
    private const int ExitAlreadyRunning = 4;
    private const string ProbeFileName = "probe.txt";
    private const string ProductName = "Command and Conquer Zero Hour RoTR Trainer";

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [STAThread]
    private static int Main(string[] args)
    {
        SetDefaultDllDirectories(LoadLibrarySearchSystem32);

        if (IsElevated() && FolderIsWritableByNonAdmins(AppContext.BaseDirectory))
        {
            System.Windows.MessageBox.Show(
                "The trainer runs as administrator, so it will not start from a folder that ordinary programs can change.\n\n"
                + "Move the whole folder to a place only administrators can write to, for example C:\\Program Files\\RoTR-Trainer, and start it from there.",
                ProductName);
            return ExitUnsafeFolder;
        }

        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\ZKidzDev.RotrTrainer", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show("The trainer is already running.", ProductName);
            return ExitAlreadyRunning;
        }

        bool includeCodePatches = args.Contains(CodePatchesFlag);
        var features = CreateFeatures(includeCodePatches);
        using var engine = CreateEngine(features, allowSuspend: includeCodePatches);
        if (args.Contains(ProbeFlag))
            return RunProbe(engine);

        new System.Windows.Application().Run(new MainWindow(engine, features));
        return ExitOk;
    }

    // Read-only: nothing is armed and no feature is enabled. Writes beside the exe, never to a caller-chosen path.
    private static int RunProbe(TrainerEngine engine)
    {
        for (int elapsedMs = 0; elapsedMs < ProbeDurationMs; elapsedMs += TickIntervalMs)
        {
            engine.Tick();
            Thread.Sleep(TickIntervalMs);
        }

        var status = engine.Status;
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, ProbeFileName),
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

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // Anyone who can change this folder can run code as administrator at the next start.
    private static bool FolderIsWritableByNonAdmins(string folder)
    {
        const FileSystemRights WriteRights = FileSystemRights.WriteData | FileSystemRights.AppendData
            | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        var trusted = new[]
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
        };

        var rules = new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & WriteRights) != 0
                && !trusted.Contains((SecurityIdentifier)rule.IdentityReference))
            {
                return true;
            }
        }
        return false;
    }

    private static TrainerEngine CreateEngine(IReadOnlyList<IFeature> features, bool allowSuspend) =>
        new(features, (out string failure) => AttachToGame(allowSuspend, out failure), NetworkEndpoints.OwnsAny);

    private static AttachedGame? AttachToGame(bool allowSuspend, out string failure)
    {
        var memory = Win32ProcessMemory.TryAttach(allowSuspend, out failure);
        return memory is null ? null : new AttachedGame(memory, memory.ExePath, memory.ProcessId, memory.StartTimeUtc);
    }

    // Order is the display order and the hotkey order: F6-F10, then Insert.
    private static IReadOnlyList<IFeature> CreateFeatures(bool includeCodePatches) =>
        includeCodePatches
            // Diagnostic set: the code patches alone, to find out why they black out the picture.
            ? [new InstantBuild(), new NoCooldown(), new UnlimitedPower(), new CrashFix()]
            : [new MoneyFloor(), new GeneralPoints(), new FastBuild(), new FreeBuild(), new PowerBonus(), new SharedPowerCooldown()];
}
