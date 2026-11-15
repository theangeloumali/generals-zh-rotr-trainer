using RotrTrainer.Core;

namespace RotrTrainer.Tests;

public static class CoreTests
{
    internal const string GameFolder =
        @"C:\Program Files (x86)\Steam\steamapps\common\Command & Conquer Generals - Zero Hour\";

    private static PeImage LoadImage(string fileName)
    {
        Check.True(PeImage.TryParse(File.ReadAllBytes(GameFolder + fileName), out var image), $"{fileName} parses");
        return image!;
    }

    // ---- PeImage ----

    public static void TestPeImageParsesModdedExe()
    {
        var image = LoadImage("modded.exe");
        Check.Equal(0x400000u, image.ImageBase, "image base");
        Check.Equal(0x200u, image.FileAlignment, "file alignment");
        Check.Equal(6, image.Sections.Count, "section count");
        Check.Equal(0x401000u, image.TextVa, ".text VA");
        Check.Equal(0x537981, image.Text.Length, ".text length");
        Check.Equal(0x55E60u, image.VaToFileOffset(0x456A60), "VA to file offset");
        Check.Equal(0x456A60u, image.FileOffsetToVa(0x55E60), "file offset to VA");
    }

    public static void TestPeImageParsesGameDatWithLargeFileAlignment()
    {
        var image = LoadImage("Game.dat");
        Check.Equal(0x1000u, image.FileAlignment, "file alignment");
        Check.Equal(0x56820u, image.VaToFileOffset(0x456820), "VA to file offset");
        Check.Equal(0x456820u, image.FileOffsetToVa(0x56820), "file offset to VA");
    }

    public static void TestPeImageMapsNothingOutsideSections()
    {
        var image = LoadImage("modded.exe");
        Check.Equal<uint?>(null, image.VaToFileOffset(0x10), "VA below the image");
        Check.Equal<uint?>(null, image.FileOffsetToVa(0x10), "offset inside the headers");
    }

    public static void TestPeImageRejectsGarbageAndTruncatedFiles()
    {
        Check.True(!PeImage.TryParse(new byte[64], out _), "zero bytes are not a PE");
        var headersOnly = File.ReadAllBytes(GameFolder + "modded.exe")[..0x400];
        Check.True(!PeImage.TryParse(headersOnly, out _), "sections past the end of the file are refused");
    }

    // ---- SignatureScanner ----

    private static readonly byte[] Haystack = [0x90, 0xA1, 0x10, 0x20, 0x8B, 0x90, 0xA1, 0x33, 0x44, 0x8C];

    public static void TestSignatureFindsSingleMatchWithWildcards()
    {
        int? offset = SignatureScanner.FindUnique(Haystack, BytePattern.Parse("A1 ?? ?? 8B"), out int count);
        Check.Equal(1, count, "match count");
        Check.Equal<int?>(1, offset, "offset");
    }

    public static void TestSignatureMatchingTwiceIsAFailure()
    {
        int? offset = SignatureScanner.FindUnique(Haystack, BytePattern.Parse("A1 ?? ??"), out int count);
        Check.Equal(2, count, "match count");
        Check.Equal<int?>(null, offset, "no offset for an ambiguous match");
    }

    public static void TestSignatureMatchingNothingIsAFailure()
    {
        int? offset = SignatureScanner.FindUnique(Haystack, BytePattern.Parse("A1 ?? ?? 8D"), out int count);
        Check.Equal(0, count, "match count");
        Check.Equal<int?>(null, offset, "no offset");
    }

    public static void TestSignatureAtEndOfDataStillMatches()
    {
        Check.Equal<int?>(8, SignatureScanner.FindUnique(Haystack, BytePattern.Parse("44 8C"), out _), "tail match");
    }

    public static void TestBytePatternRejectsBadTokensAndAllWildcards()
    {
        foreach (string bad in new[] { "", "A1 G0", "A1 1", "?? ??" })
        {
            bool threw = false;
            try { BytePattern.Parse(bad); }
            catch (ArgumentException) { threw = true; }
            Check.True(threw, $"pattern '{bad}' is refused");
        }
    }

    // ---- BuildCatalog ----

    private static GameAddresses Resolved(string fileName)
    {
        var result = BuildCatalog.Resolve(GameFolder + fileName);
        Check.True(result.Addresses is not null, $"{fileName} resolves (refusal: {result.Refusal})");
        return result.Addresses!;
    }

    public static void TestCatalogResolvesModdedExe()
    {
        var addresses = Resolved("modded.exe");
        Check.Equal(0x00A2B600u, addresses.PlayerListPointer, "PlayerListPointer");
        Check.Equal(0x00A2BB64u, addresses.GameLogicPointer, "GameLogicPointer");
        Check.Equal(0x0058C8A0u, addresses.CalcTimeToBuild, "CalcTimeToBuild");
        Check.Equal(0x004AEAF0u, addresses.UpgradeCalcTimeToBuild, "UpgradeCalcTimeToBuild");
        Check.Equal(0x00686E10u, addresses.StartPowerRecharge, "StartPowerRecharge");
        Check.Equal(0x00456A60u, addresses.OnPowerBrownOutChange, "OnPowerBrownOutChange");
        Check.Equal(0x005D4C1Fu, addresses.MoveAwayFromUnitNullSite, "MoveAwayFromUnitNullSite");
    }

    public static void TestCatalogResolvesGameDat()
    {
        var addresses = Resolved("Game.dat");
        Check.Equal(0x00A2B688u, addresses.PlayerListPointer, "PlayerListPointer");
        Check.Equal(0x00A2BBECu, addresses.GameLogicPointer, "GameLogicPointer");
        Check.Equal(0x0058C650u, addresses.CalcTimeToBuild, "CalcTimeToBuild");
        Check.Equal(0x004AEC30u, addresses.UpgradeCalcTimeToBuild, "UpgradeCalcTimeToBuild");
        Check.Equal(0x00686F60u, addresses.StartPowerRecharge, "StartPowerRecharge");
        Check.Equal(0x00456820u, addresses.OnPowerBrownOutChange, "OnPowerBrownOutChange");
        Check.Equal(0x005D4A0Fu, addresses.MoveAwayFromUnitNullSite, "MoveAwayFromUnitNullSite");
    }

    public static void TestEverySignatureMatchesExactlyOnceInBothBuilds()
    {
        foreach (string fileName in new[] { "modded.exe", "Game.dat" })
        {
            var image = LoadImage(fileName);
            foreach (var site in BuildCatalog.Sites)
            {
                SignatureScanner.FindUnique(image.Text, site.Pattern, out int count);
                Check.Equal(1, count, $"{site.Name} in {fileName}");
            }
        }
    }

    public static void TestCatalogRefusesUnknownHashes()
    {
        foreach (string fileName in new[] { "Game.dat.bak", "generalszh.exe" })
        {
            var result = BuildCatalog.Resolve(GameFolder + fileName);
            Check.True(result.Addresses is null, $"{fileName} is refused");
            Check.True(result.Refusal!.Contains("allow-list"), $"{fileName} refusal names the allow-list: {result.Refusal}");
        }
    }

    public static void TestCatalogRefusesMissingFile()
    {
        var result = BuildCatalog.Resolve(GameFolder + "no-such-file.exe");
        Check.True(result.Addresses is null && result.Refusal is not null, "missing file is refused");
    }

    private static PeImage PatchedModded(Action<byte[], PeImage> patch)
    {
        byte[] bytes = File.ReadAllBytes(GameFolder + "modded.exe");
        Check.True(PeImage.TryParse(bytes, out var original), "modded.exe parses");
        patch(bytes, original!);
        Check.True(PeImage.TryParse(bytes, out var patched), "patched copy parses");
        return patched!;
    }

    public static void TestCatalogRefusesWhenASignatureMatchesNothing()
    {
        var image = PatchedModded((bytes, original) => bytes[original.VaToFileOffset(0x0058C8A0)!.Value] = 0xCC);
        var result = BuildCatalog.ResolveImage(image, "patched");
        Check.True(result.Addresses is null, "refused");
        Check.True(result.Refusal!.Contains("CalcTimeToBuild") && result.Refusal.Contains("0 matches"), result.Refusal);
    }

    public static void TestCatalogRefusesWhenASignatureMatchesTwice()
    {
        var image = PatchedModded((bytes, original) =>
        {
            int source = (int)original.VaToFileOffset(0x0058C8A0)!.Value;
            int target = (int)original.VaToFileOffset(original.TextVa + 0x100)!.Value;
            Array.Copy(bytes, source, bytes, target, 32);
        });
        var result = BuildCatalog.ResolveImage(image, "patched");
        Check.True(result.Addresses is null, "refused");
        Check.True(result.Refusal!.Contains("CalcTimeToBuild") && result.Refusal.Contains("2 matches"), result.Refusal);
    }

    // ---- GameState + Guards on FakeMemory ----

    private const uint GameLogicObject = 0x20000000;
    private const uint PlayerListObject = 0x21000000;
    private const uint LocalPlayerObject = 0x22000000;

    private static readonly GameAddresses TestAddresses = new()
    {
        ImageCheckAddress = 0x00401000,
        ImageCheckBytes = [0xA1],
        BuildName = "test",
        PlayerListPointer = 0x00A2B600,
        GameLogicPointer = 0x00A2BB64,
        CalcTimeToBuild = 0x0058C8A0,
        UpgradeCalcTimeToBuild = 0x004AEAF0,
        StartPowerRecharge = 0x00686E10,
        OnPowerBrownOutChange = 0x00456A60,
        MoveAwayFromUnitNullSite = 0x005D4C1F,
        SpecialPowerCtorRechargeSite = 0x006867FD,
        PlayerUpdateBrownOutSite = 0x00451193,
        GetEnergySupplyRatio = 0x00577200,
        MoveAwayFromUnitExit = 0x005D4E17,
        GetControllingPlayer = 0x00547C30,
        SpecialPowerCtorRechargeSkip = 0x0068681C,
        UpgradeBuildTimeConstant = 0x0093BEDC,
    };

    private static FakeMemory GameInProgress(GameMode mode)
    {
        var memory = new FakeMemory();
        memory.MapUInt32(TestAddresses.GameLogicPointer, GameLogicObject);
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicFrame, 1234);
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicGameMode, (uint)mode);
        memory.MapUInt32(TestAddresses.PlayerListPointer, PlayerListObject);
        memory.MapUInt32(PlayerListObject + GameAddresses.PlayerListLocalPlayer, LocalPlayerObject);
        // A live human player: type 0, not an observer, not dead.
        memory.MapUInt32(LocalPlayerObject + GameAddresses.PlayerType, 0);
        memory.Map(LocalPlayerObject + GameAddresses.PlayerIsObserver, [0]);
        memory.Map(LocalPlayerObject + GameAddresses.PlayerIsDead, [0]);
        return memory;
    }

    public static void TestGameStateReadsALiveSkirmish()
    {
        var state = new GameState(GameInProgress(GameMode.Skirmish), TestAddresses);
        Check.Equal<uint?>(GameLogicObject, state.GameLogic(), "game logic");
        Check.Equal<uint?>(1234, state.Frame(), "frame");
        Check.Equal<GameMode?>(GameMode.Skirmish, state.Mode(), "mode");
        Check.Equal<uint?>(LocalPlayerObject, state.LocalPlayer(), "local player");
    }

    public static void TestGameStateAtMainMenuReportsNullPointersAsZero()
    {
        var memory = new FakeMemory();
        memory.MapUInt32(TestAddresses.GameLogicPointer, 0);
        memory.MapUInt32(TestAddresses.PlayerListPointer, 0);
        var state = new GameState(memory, TestAddresses);
        Check.Equal<uint?>(0, state.GameLogic(), "game logic");
        Check.Equal<uint?>(0, state.LocalPlayer(), "local player");
        Check.Equal<uint?>(null, state.Frame(), "no frame without game logic");
        Check.Equal<GameMode?>(null, state.Mode(), "no mode without game logic");
    }

    public static void TestGameStateReadFailureIsNullNotAnException()
    {
        var memory = GameInProgress(GameMode.Skirmish);
        memory.FailReads = true;
        var state = new GameState(memory, TestAddresses);
        Check.Equal<uint?>(null, state.GameLogic(), "game logic");
        Check.Equal<uint?>(null, state.LocalPlayer(), "local player");
        Check.Equal<uint?>(null, state.Frame(), "frame");
        Check.Equal<GameMode?>(null, state.Mode(), "mode");
    }

    public static void TestGameStateUnknownModeValueIsNull()
    {
        var memory = GameInProgress(GameMode.Skirmish);
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicGameMode, 9);
        Check.Equal<GameMode?>(null, new GameState(memory, TestAddresses).Mode(), "mode 9");
    }

    private static GuardVerdict Evaluate(FakeMemory memory, bool? ownsNetwork = false) =>
        Guards.Evaluate(memory, TestAddresses, () => ownsNetwork);

    private static void ExpectRefused(GuardVerdict verdict, string reasonFragment)
    {
        Check.True(!verdict.MayArm, $"refused ({reasonFragment})");
        Check.True(verdict.Reason.Contains(reasonFragment), $"reason '{verdict.Reason}' mentions '{reasonFragment}'");
    }

    public static void TestGuardsAllowSkirmishAndSinglePlayer()
    {
        foreach (var mode in new[] { GameMode.Skirmish, GameMode.SinglePlayer })
            Check.True(Evaluate(GameInProgress(mode)).MayArm, $"{mode} may arm");
    }

    public static void TestGuardsRefuseLanReplayShellAndInternet()
    {
        foreach (var mode in new[] { GameMode.Lan, GameMode.Replay, GameMode.Shell, GameMode.Internet })
            ExpectRefused(Evaluate(GameInProgress(mode)), mode.ToString());
    }

    public static void TestGuardsRefuseNullLocalPlayer()
    {
        var memory = GameInProgress(GameMode.Skirmish);
        memory.MapUInt32(PlayerListObject + GameAddresses.PlayerListLocalPlayer, 0);
        ExpectRefused(Evaluate(memory), "local player");
    }

    public static void TestGuardsRefuseNullPlayerList()
    {
        var memory = GameInProgress(GameMode.Skirmish);
        memory.MapUInt32(TestAddresses.PlayerListPointer, 0);
        ExpectRefused(Evaluate(memory), "local player");
    }

    public static void TestGuardsRefuseNullGameLogic()
    {
        var memory = GameInProgress(GameMode.Skirmish);
        memory.MapUInt32(TestAddresses.GameLogicPointer, 0);
        ExpectRefused(Evaluate(memory), "no game");
    }

    public static void TestGuardsRefuseFailedReads()
    {
        var memory = GameInProgress(GameMode.Skirmish);
        memory.FailReads = true;
        ExpectRefused(Evaluate(memory), "read");
    }

    public static void TestGuardsRefuseDeadProcess()
    {
        var memory = GameInProgress(GameMode.Skirmish);
        memory.IsAlive = false;
        ExpectRefused(Evaluate(memory), "not running");
    }

    public static void TestGuardsRefuseNetworkEndpointsAndFailedQuery()
    {
        ExpectRefused(Evaluate(GameInProgress(GameMode.Skirmish), ownsNetwork: true), "network");
        ExpectRefused(Evaluate(GameInProgress(GameMode.Skirmish), ownsNetwork: null), "network");
    }

    // ---- NetworkEndpoints + Win32ProcessMemory against this test process (never the game) ----

    public static void TestNetworkEndpointsSeeThisProcessUdpSocket()
    {
        using var socket = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        Check.Equal<bool?>(true, NetworkEndpoints.OwnsAny(Environment.ProcessId), "own UDP socket is seen");
    }

    public static void TestNetworkEndpointsReportFalseForPidWithNoSockets()
    {
        // Windows pids are multiples of 4, so 3 never owns anything.
        Check.Equal<bool?>(false, NetworkEndpoints.OwnsAny(3), "pid 3");
    }

    public static void TestWin32OpenReportsPathStartTimeAndLiveness()
    {
        var memory = Win32ProcessMemory.Open(Environment.ProcessId, allowSuspend: false, out string failure);
        Check.True(memory is not null, $"opens own process ({failure})");
        using (memory)
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            Check.Equal(Environment.ProcessId, memory!.ProcessId, "pid");
            Check.Equal(self.MainModule!.FileName, memory.ExePath, "exe path");
            Check.Equal(self.StartTime.ToUniversalTime(), memory.StartTimeUtc, "start time");
            Check.True(memory.IsAlive, "alive");
            Check.True(!memory.TryRead(0x10, new byte[4]), "null page read fails");
        }
        Check.True(!memory!.IsAlive, "disposed handle is not alive");
    }

    public static void TestWin32OpenFailsForMissingPid()
    {
        Check.True(Win32ProcessMemory.Open(3, allowSuspend: false, out string failure) is null, "pid 3 cannot be opened");
        Check.True(failure.Length > 0, "failure has a reason");
    }
}
