using System.Buffers.Binary;
using RotrTrainer.Core;
using RotrTrainer.Core.Features;
using static RotrTrainer.Core.Register32;

namespace RotrTrainer.Tests;

public static class CodePatchTests
{
    private const uint TestSite = 0x00401000;
    private const uint TestCave = 0x30001000;
    private const uint TestGuard = 0x30000000;
    private const uint LocalPlayer = 0x22000000;
    private static readonly byte[] TestOriginals = [0x8A, 0x44, 0x24, 0x04, 0x84, 0xC0];

    private static void Bytes(string expectedHex, ReadOnlySpan<byte> actual, string what) =>
        Check.Equal(expectedHex.Replace(" ", ""), Convert.ToHexString(actual), what);

    private static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException($"{what}: expected {typeof(TException).Name}");
    }

    // ---- X86 encodings ----

    public static void TestX86EncodingsMatchKnownBytes()
    {
        Bytes("E9 DC C3 A2 2F", X86.JumpRel32(0x005D4C1F, TestCave), "jmp site -> cave");
        Bytes("E8 1B 6C 54 D0", X86.CallRel32(0x30001010, 0x00547C30), "call backwards");
        Bytes("0F 85 22 02 00 00", X86.JumpIfRel32(Condition.NotEqual, 0x005D4BEF, 0x005D4E17), "jne rel32 (modded 0x5D4BEF)");
        Bytes("74 10", X86.JumpIfShort(Condition.Equal, 0x10), "je short");
        Bytes("75 FE", X86.JumpIfShort(Condition.NotEqual, -2), "jne short back");
        Bytes("72 05", X86.JumpIfShort(Condition.Below, 5), "jb short");
        Bytes("73 05", X86.JumpIfShort(Condition.AboveOrEqual, 5), "jae short");
        Bytes("8B 0D 64 BB A2 00", X86.MovFromAbsolute(Ecx, 0x00A2BB64), "mov ecx,[TheGameLogic] (ctor stolen bytes)");
        Bytes("8B 54 24 18", X86.MovFromMemory(Edx, Esp, 0x18), "mov edx,[esp+18h] (crash-fix stolen bytes)");
        Bytes("8B 4A 64", X86.MovFromMemory(Ecx, Edx, 0x64), "mov ecx,[edx+64h] (crash-fix stolen bytes)");
        Bytes("8B 90 94 00 00 00", X86.MovFromMemory(Edx, Eax, 0x94), "mov edx,[eax+94h]");
        Bytes("8B 51 F8", X86.MovFromMemory(Edx, Ecx, -8), "mov edx,[ecx-8]");
        Bytes("3B 15 78 56 34 12", X86.CmpWithAbsolute(Edx, 0x12345678), "cmp edx,[abs]");
        Bytes("3B 54 24 04", X86.CmpWithMemory(Edx, Esp, 4), "cmp edx,[esp+4]");
        Bytes("3B 51 10", X86.CmpWithMemory(Edx, Ecx, 0x10), "cmp edx,[ecx+10h]");
        Bytes("3B EA", X86.CmpRegisters(Ebp, Edx), "cmp ebp,edx");
        Bytes("3B C2", X86.CmpRegisters(Eax, Edx), "cmp eax,edx");
        Bytes("83 FA 02", X86.CmpImmediate8(Edx, 2), "cmp edx,2");
        Bytes("80 3D 78 56 34 12 00", X86.CmpByteWithZero(0x12345678), "cmp byte [abs],0");
        Bytes("85 C0", X86.Test(Eax), "test eax,eax");
        Bytes("85 D2", X86.Test(Edx), "test edx,edx");
        Bytes("8B CA", X86.MovRegister(Ecx, Edx), "mov ecx,edx");
        Bytes("B8 01 00 00 00", X86.MovImmediate(Eax, 1), "mov eax,1");
        Bytes("51", X86.Push(Ecx), "push ecx");
        Bytes("59", X86.Pop(Ecx), "pop ecx");
        Bytes("C3", X86.Return(), "ret");
        Bytes("C2 04 00", X86.ReturnPop(4), "ret 4");
        Bytes("C6 44 24 04 00", X86.MovByteToStack(4, 0), "mov byte [esp+4],0");
        Bytes("32 C0", X86.ClearAl(), "xor al,al");
        Bytes("D9 E8", X86.LoadOne(), "fld1");
    }

    public static void TestCallEncodingMatchesTheGamesOwnCall()
    {
        // startPowerRecharge+16h calls getControllingPlayer in both builds.
        foreach (var build in Builds.Value)
        {
            uint callSite = build.Addresses.StartPowerRecharge + 0x16;
            Bytes(Convert.ToHexString(build.BytesAt(callSite, 5)),
                X86.CallRel32(callSite, build.Addresses.GetControllingPlayer), $"call in {build.FileName}");
        }
    }

    public static void TestCaveAssemblerResolvesForwardBranchesAndRejectsBadOnes()
    {
        var skip = new CaveLabel();
        var cave = new CaveAssembler(TestCave).JumpIf(Condition.Equal, skip).Emit([X86.Nop, X86.Nop]).Place(skip).JumpTo(TestCave);
        Bytes("74 02 90 90 E9 F7 FF FF FF", cave.ToArray(), "je over two nops, then jmp back to the start");

        var far = new CaveLabel();
        var farCave = new CaveAssembler(TestCave).JumpIf(Condition.Equal, far).Emit(new byte[200]);
        Throws<InvalidOperationException>(() => farCave.Place(far), "a short branch past 127 bytes");

        var unplaced = new CaveAssembler(TestCave).JumpIf(Condition.Equal, new CaveLabel());
        Throws<InvalidOperationException>(() => unplaced.ToArray(), "a branch to a label never placed");
    }

    // ---- CodePatch on FakeMemory ----

    private static CodePatch TestPatch() => new("test site", TestSite, TestOriginals, (cave, context) => { });

    private static FakeMemory MemoryWithSite(byte[] siteBytes)
    {
        var memory = new FakeMemory();
        memory.Map(TestSite, siteBytes);
        return memory;
    }

    private static List<FakeWrite> SiteWrites(FakeMemory memory) => memory.Writes.Where(write => write.Address == TestSite).ToList();

    private static string JumpToCaveHex(CodePatch patch) => Convert.ToHexString(X86.JumpRel32(TestSite, patch.CaveAddress)) + "90";

    public static void TestInstallWritesTheJumpOnlyWhileSuspendedAndReadsItBack()
    {
        var memory = MemoryWithSite(TestOriginals);
        var patch = TestPatch();
        var result = patch.Install(memory);
        Check.True(result.Succeeded && patch.IsInstalled, $"installed: {result.Message}");
        Bytes(JumpToCaveHex(patch), memory.Peek(TestSite, 6), "site holds jmp cave + nop");
        Check.True(SiteWrites(memory).Count == 1 && SiteWrites(memory).All(write => write.WhileSuspended), "site written once, suspended");
        Check.True(memory.ProtectionChanges.Contains(new FakeProtection(patch.CaveAddress, 0x1000, PageAccess.ExecuteRead)),
            "cave page switched to ExecuteRead");
        Check.True(memory.Flushes.Contains((TestSite, 6)), "instruction cache flushed for the site");
        Check.Equal(memory.SuspendCount, memory.ResumeCount, "resumed");
        Bytes(Convert.ToHexString(TestOriginals), memory.Peek(patch.CaveAddress, 6), "cave starts with the fall-through");
    }

    public static void TestInstallRefusesUnexpectedSiteBytesAndWritesNothingThere()
    {
        foreach (byte[] siteBytes in new[] { new byte[] { 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC }, [0xE9, 0x11, 0x22, 0x33, 0x44, 0xCC] })
        {
            var memory = MemoryWithSite(siteBytes);
            var result = TestPatch().Install(memory);
            Check.True(!result.Succeeded && result.Message.Contains("test site"), $"refused: {result.Message}");
            Check.Equal(0, SiteWrites(memory).Count, "no site writes");
            Check.Equal(0, memory.SuspendCount, "never suspended");
        }
    }

    public static void TestTornWriteRestoresTheOriginalsAndResumes()
    {
        var inner = MemoryWithSite(TestOriginals);
        var memory = new FaultyMemory(inner) { TearJumpsAt = TestSite };
        var patch = TestPatch();
        var result = patch.Install(memory);
        Check.True(!result.Succeeded && !patch.IsInstalled, $"failed: {result.Message}");
        Bytes(Convert.ToHexString(TestOriginals), inner.Peek(TestSite, 6), "originals restored");
        Check.True(SiteWrites(inner).All(write => write.WhileSuspended), "every site write suspended");
        Check.Equal(inner.SuspendCount, inner.ResumeCount, "resumed after the failure");
    }

    public static void TestProcessDeathMidInstallReportsFailureAndResumes()
    {
        var inner = MemoryWithSite(TestOriginals);
        var result = TestPatch().Install(new FaultyMemory(inner) { DieAfterWriteTo = TestSite });
        Check.True(!result.Succeeded && result.Message.Contains("restoring"), $"failed: {result.Message}");
        Check.Equal(1, inner.ResumeCount, "resumed even though the process died");
    }

    public static void TestSuspendOrAllocationFailureLeavesTheSiteUntouched()
    {
        var suspendFails = MemoryWithSite(TestOriginals);
        var result = TestPatch().Install(new FaultyMemory(suspendFails) { FailSuspend = true });
        Check.True(!result.Succeeded && result.Message.Contains("suspend"), $"suspend failure reported: {result.Message}");
        Check.Equal(0, SiteWrites(suspendFails).Count, "no site writes when suspend fails");
        Check.Equal(0, suspendFails.ResumeCount, "nothing to resume");

        var allocationFails = MemoryWithSite(TestOriginals);
        allocationFails.FailAllocations = true;
        Check.True(!TestPatch().Install(allocationFails).Succeeded, "allocation failure reported");
        Check.Equal(0, SiteWrites(allocationFails).Count, "no site writes when the cave cannot be allocated");
    }

    public static void TestUninstallRestoresExactlyAndIsSafeTwice()
    {
        var memory = MemoryWithSite(TestOriginals);
        var untouched = TestPatch();
        Check.True(untouched.Uninstall(memory).Succeeded, "uninstall before install is a no-op");
        Check.Equal(0, memory.Writes.Count, "nothing written for a patch never installed");

        var patch = TestPatch();
        patch.Install(memory);
        patch.Arm(memory, 100);
        Check.True(patch.Uninstall(memory).Succeeded && !patch.IsInstalled, "uninstalled");
        Bytes(Convert.ToHexString(TestOriginals), memory.Peek(TestSite, 6), "originals back");
        Check.Equal((byte)0, memory.Peek(patch.CaveAddress - 0x1000, 1)[0], "armed byte cleared");
        Check.True(SiteWrites(memory).All(write => write.WhileSuspended), "restore written while suspended");

        int writesBefore = SiteWrites(memory).Count;
        Check.True(patch.Uninstall(memory).Succeeded, "second uninstall succeeds");
        Check.Equal(writesBefore, SiteWrites(memory).Count, "second uninstall writes nothing to the site");
    }

    public static void TestLeftoverJumpIsRestoredThenReinstalled()
    {
        var memory = MemoryWithSite([0xE9, 0x11, 0x22, 0x33, 0x44, 0x90]);
        var patch = TestPatch();
        var result = patch.Install(memory);
        Check.True(result.Succeeded, $"installed over a leftover: {result.Message}");
        var writes = SiteWrites(memory);
        Check.Equal(2, writes.Count, "restore, then hook");
        Bytes(Convert.ToHexString(TestOriginals), writes[0].Data, "first write restores the originals");
        Bytes(JumpToCaveHex(patch), writes[1].Data, "second write is the new jump");
    }

    public static void TestArmWritesTheDeadManWindow()
    {
        var memory = MemoryWithSite(TestOriginals);
        var patch = TestPatch();
        Check.True(!patch.Arm(memory, 1000), "nothing to arm before install");
        patch.Install(memory);
        Check.True(patch.Arm(memory, 1000), "armed");
        var guard = GuardOf(memory, TestSite);
        Check.Equal((byte)1, memory.Peek(guard.Armed, 1)[0], "armed byte");
        Check.Equal(1000u, ReadUInt32(memory, guard.StartFrame), "start frame");
        Check.Equal(1000u + CodePatch.DeadManFrames, ReadUInt32(memory, guard.ExpiryFrame), "expiry frame");
    }

    // ---- Patch sites against the real files ----

    private sealed record BuildUnderTest(string FileName, byte[] FileBytes, PeImage Image, GameAddresses Addresses)
    {
        public byte[] BytesAt(uint va, int count) =>
            FileBytes.AsSpan((int)Image.VaToFileOffset(va)!.Value, count).ToArray();
    }

    private static readonly Lazy<BuildUnderTest[]> Builds = new(() => new[] { "modded.exe", "Game.dat" }.Select(fileName =>
    {
        byte[] fileBytes = File.ReadAllBytes(CoreTests.GameFolder + fileName);
        Check.True(PeImage.TryParse(fileBytes, out var image), $"{fileName} parses");
        var result = BuildCatalog.ResolveImage(image!, fileName);
        Check.True(result.Addresses is not null, $"{fileName} resolves: {result.Refusal}");
        return new BuildUnderTest(fileName, fileBytes, image!, result.Addresses!);
    }).ToArray());

    private static IReadOnlyList<CodePatch> CheatPatches(GameAddresses addresses) =>
        [.. InstantBuild.CreatePatches(addresses), .. NoCooldown.CreatePatches(addresses), .. UnlimitedPower.CreatePatches(addresses)];

    private static IReadOnlyList<CodePatch> AllPatches(GameAddresses addresses) =>
        [.. CrashFix.CreatePatches(addresses), .. CheatPatches(addresses)];

    // Span length and the instruction that starts right after it, per docs/anchors.md.
    private static readonly Dictionary<string, (int SpanLength, string NextInstruction)> AnchorSpans = new()
    {
        ["AIUpdateInterface::privateMoveAwayFromUnit"] = (7, "74 65"),
        ["ThingTemplate::calcTimeToBuild"] = (5, "57 8B 7C 24 18"),
        ["UpgradeTemplate::calcTimeToBuild"] = (9, "E8"),
        ["SpecialPowerModule::startPowerRecharge"] = (5, "8B 7E F4"),
        ["SpecialPowerModule constructor recharge"] = (6, "8B 59 3C"),
        ["Player::onPowerBrownOutChange"] = (6, "56 57 8B F9"),
        ["Player::update brown-out"] = (6, "88 44 24 13"),
        ["Energy::getEnergySupplyRatio"] = (6, "8B 50 3C"),
    };

    public static void TestNewSitesResolveToTheAnchorAddresses()
    {
        var (modded, gameDat) = (Builds.Value[0].Addresses, Builds.Value[1].Addresses);
        Check.Equal(0x005D4E17u, modded.MoveAwayFromUnitExit, "modded MoveAwayFromUnitExit");
        Check.Equal(0x00547C30u, modded.GetControllingPlayer, "modded GetControllingPlayer");
        Check.Equal(0x006867FDu, modded.SpecialPowerCtorRechargeSite, "modded ctor site");
        Check.Equal(0x0068681Cu, modded.SpecialPowerCtorRechargeSkip, "modded ctor skip");
        Check.Equal(0x00451193u, modded.PlayerUpdateBrownOutSite, "modded Player::update site");
        Check.Equal(0x00577200u, modded.GetEnergySupplyRatio, "modded getEnergySupplyRatio");
        Check.Equal(0x0093BEDCu, modded.UpgradeBuildTimeConstant, "modded 30.0f");
        Check.Equal(0x005D4C07u, gameDat.MoveAwayFromUnitExit, "Game.dat MoveAwayFromUnitExit");
        Check.Equal(0x00547A00u, gameDat.GetControllingPlayer, "Game.dat GetControllingPlayer");
        Check.Equal(0x0068694Du, gameDat.SpecialPowerCtorRechargeSite, "Game.dat ctor site");
        Check.Equal(0x0068696Cu, gameDat.SpecialPowerCtorRechargeSkip, "Game.dat ctor skip");
        Check.Equal(0x00450F53u, gameDat.PlayerUpdateBrownOutSite, "Game.dat Player::update site");
        Check.Equal(0x00576F20u, gameDat.GetEnergySupplyRatio, "Game.dat getEnergySupplyRatio");
        Check.Equal(0x0093BEE4u, gameDat.UpgradeBuildTimeConstant, "Game.dat 30.0f");
    }

    public static void TestEveryPatchSiteMatchesTheFileOnAnInstructionBoundary()
    {
        foreach (var build in Builds.Value)
        {
            var patches = AllPatches(build.Addresses);
            Check.Equal(AnchorSpans.Count, patches.Count, "one patch per anchors.md site");
            foreach (var patch in patches)
            {
                var (spanLength, nextInstruction) = AnchorSpans[patch.Name];
                string where = $"{patch.Name} in {build.FileName}";
                Check.Equal(spanLength, patch.OriginalBytes.Length, $"{where} span");
                Bytes(Convert.ToHexString(build.BytesAt(patch.Site, spanLength)), patch.OriginalBytes, $"{where} originals");
                int nextLength = nextInstruction.Split(' ').Length;
                Bytes(nextInstruction, build.BytesAt(patch.ReturnAddress, nextLength), $"{where} next instruction");
            }
        }
    }

    public static void TestEveryCaveFallsThroughToTheOriginalsAndRelocatesItsBranches()
    {
        foreach (var build in Builds.Value)
        {
            var addresses = build.Addresses;
            foreach (var patch in AllPatches(addresses))
            {
                var cave = patch.BuildCave(TestCave, new GuardBlock(TestGuard));
                string where = $"{patch.Name} in {build.FileName}";
                int span = patch.OriginalBytes.Length;
                Bytes(Convert.ToHexString(patch.OriginalBytes), cave.Code.AsSpan(cave.FallThroughOffset, span), $"{where} fall-through");
                uint jumpBack = TestCave + (uint)(cave.FallThroughOffset + span);
                Bytes(Convert.ToHexString(X86.JumpRel32(jumpBack, patch.ReturnAddress)),
                    cave.Code.AsSpan(cave.FallThroughOffset + span), $"{where} jumps back to the return address");
            }
            var crashCave = CrashFix.CreatePatches(addresses)[0].BuildCave(TestCave, new GuardBlock(TestGuard)).Code;
            Check.True(HasBranchTo(crashCave, [0x0F, 0x84], addresses.MoveAwayFromUnitExit), $"crash fix je exit in {build.FileName}");
            var cooldown = NoCooldown.CreatePatches(addresses);
            Check.True(HasBranchTo(cooldown[0].BuildCave(TestCave, new GuardBlock(TestGuard)).Code, [0xE8], addresses.GetControllingPlayer),
                $"recharge calls getControllingPlayer in {build.FileName}");
            Check.True(HasBranchTo(cooldown[1].BuildCave(TestCave, new GuardBlock(TestGuard)).Code, [0xE9], addresses.SpecialPowerCtorRechargeSkip),
                $"ctor cave jumps to the join in {build.FileName}");
        }
    }

    // True when some rel32 branch with this opcode in the cave decodes to the target.
    private static bool HasBranchTo(byte[] cave, byte[] opcode, uint target)
    {
        int length = opcode.Length + 4;
        for (int start = 0; start + length <= cave.Length; start++)
        {
            if (!cave.AsSpan(start, opcode.Length).SequenceEqual(opcode))
                continue;
            int displacement = BinaryPrimitives.ReadInt32LittleEndian(cave.AsSpan(start + opcode.Length));
            if (TestCave + (uint)(start + length) + (uint)displacement == target)
                return true;
        }
        return false;
    }

    private static int UniqueOffset(byte[] cave, string pattern, string what)
    {
        int? offset = SignatureScanner.FindUnique(cave, BytePattern.Parse(pattern), out int count);
        Check.True(offset is not null, $"{what}: pattern '{pattern}' found {count} times, expected once");
        return offset!.Value;
    }

    private static string Hex(uint value) => string.Join(' ', X86.LittleEndian(value).Select(part => part.ToString("X2")));

    public static void TestEveryCheatCaveCarriesTheGuardBeforeItsCheat()
    {
        var guard = new GuardBlock(TestGuard);
        foreach (var build in Builds.Value)
        {
            var addresses = build.Addresses;
            foreach (var patch in CheatPatches(addresses))
            {
                var cave = patch.BuildCave(TestCave, guard);
                string where = $"{patch.Name} in {build.FileName}";
                int[] order =
                [
                    UniqueOffset(cave.Code, $"8B ?? {Hex(addresses.GameLogicPointer)} 85", $"{where} TheGameLogic load + NULL test"),
                    UniqueOffset(cave.Code, "8B ?? 94 00 00 00 85 ?? 74 ?? 83 ?? 02 75", $"{where} mode is 0 or 2"),
                    UniqueOffset(cave.Code, $"80 3D {Hex(guard.Armed)} 00 74", $"{where} armed byte"),
                    UniqueOffset(cave.Code, $"8B ?? 3C 3B ?? {Hex(guard.StartFrame)} 72", $"{where} frame >= start"),
                    UniqueOffset(cave.Code, $"3B ?? {Hex(guard.ExpiryFrame)} 73", $"{where} frame < expiry"),
                    UniqueOffset(cave.Code, $"8B 15 {Hex(addresses.PlayerListPointer)} 85 D2 74 ?? 8B 52 0C 85 D2 74", $"{where} local player at call time"),
                    cave.FallThroughOffset,
                ];
                for (int i = 1; i < order.Length; i++)
                    Check.True(order[i - 1] < order[i], $"{where}: guard step {i} out of order ({string.Join(",", order)})");
            }
            var crashCave = CrashFix.CreatePatches(addresses)[0].BuildCave(TestCave, guard).Code;
            SignatureScanner.FindUnique(crashCave, BytePattern.Parse(Hex(addresses.GameLogicPointer)), out int references);
            Check.Equal(0, references, "the crash fix has no guard");
        }
    }

    public static void TestCalcTimeToBuildCaveMatchesTheReviewedBytes()
    {
        var patch = InstantBuild.CreatePatches(Builds.Value[0].Addresses)[0];
        Bytes(ReviewedCalcTimeToBuildCave, patch.BuildCave(TestCave, new GuardBlock(TestGuard)).Code, "modded calcTimeToBuild cave");
    }

    public static void TestCrashFixCaveMatchesTheReviewedBytes()
    {
        var patch = CrashFix.CreatePatches(Builds.Value[0].Addresses)[0];
        Bytes(ReviewedCrashFixCave, patch.BuildCave(TestCave, new GuardBlock(TestGuard)).Code, "modded crash-fix cave");
    }

    // ---- Features on FakeMemory (modded.exe addresses) ----

    private static FakeMemory MemoryWithOriginals(IEnumerable<CodePatch> patches)
    {
        var memory = new FakeMemory();
        foreach (var patch in patches)
            memory.Map(patch.Site, patch.OriginalBytes);
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerEnergySabotageFrame, 0);
        return memory;
    }

    private static GameContext Context(IProcessMemory memory, uint frame) =>
        new(memory, Builds.Value[0].Addresses, LocalPlayer, frame);

    private static uint ReadUInt32(FakeMemory memory, uint address) => BinaryPrimitives.ReadUInt32LittleEndian(memory.Peek(address, 4));

    // Follows the site's jump to its cave; the guard page sits one page below the cave.
    private static GuardBlock GuardOf(FakeMemory memory, uint site)
    {
        byte[] jump = memory.Peek(site, 5);
        Check.Equal((byte)0xE9, jump[0], $"0x{site:X8} holds a jump");
        uint cave = site + 5 + (uint)BinaryPrimitives.ReadInt32LittleEndian(jump.AsSpan(1));
        return new GuardBlock(cave - 0x1000);
    }

    private static readonly (IFeature Feature, Func<GameAddresses, IReadOnlyList<CodePatch>> Patches)[] CheatFeatures =
    [
        (new InstantBuild(), InstantBuild.CreatePatches),
        (new NoCooldown(), NoCooldown.CreatePatches),
        (new UnlimitedPower(), UnlimitedPower.CreatePatches),
    ];

    public static void TestCheatFeaturesInstallArmRefreshAndRemove()
    {
        foreach (var (feature, createPatches) in CheatFeatures)
        {
            var sites = createPatches(Builds.Value[0].Addresses);
            var memory = MemoryWithOriginals(sites);
            Check.True(feature.NeedsArming, $"{feature.Name} needs arming");

            feature.Apply(Context(memory, 1000));
            var guards = sites.Select(site => GuardOf(memory, site.Site)).ToList();
            foreach (var guard in guards)
            {
                Check.Equal((byte)1, memory.Peek(guard.Armed, 1)[0], $"{feature.Name} armed");
                Check.Equal(1060u, ReadUInt32(memory, guard.ExpiryFrame), $"{feature.Name} expiry = frame + 60");
            }

            feature.Apply(Context(memory, 1030));
            foreach (var guard in guards)
            {
                Check.Equal(1030u, ReadUInt32(memory, guard.StartFrame), $"{feature.Name} start refreshed");
                Check.Equal(1090u, ReadUInt32(memory, guard.ExpiryFrame), $"{feature.Name} expiry refreshed");
            }

            feature.Remove(memory);
            foreach (var (site, guard) in sites.Zip(guards))
            {
                Bytes(Convert.ToHexString(site.OriginalBytes), memory.Peek(site.Site, site.OriginalBytes.Length), $"{site.Name} restored");
                Check.Equal((byte)0, memory.Peek(guard.Armed, 1)[0], $"{site.Name} disarmed");
            }
        }
    }

    public static void TestFeatureRollsBackWhenOneSiteIsUnexpectedAndDoesNotRetry()
    {
        var sites = InstantBuild.CreatePatches(Builds.Value[0].Addresses);
        var memory = MemoryWithOriginals(sites);
        memory.Map(sites[1].Site, new byte[sites[1].OriginalBytes.Length]);
        var feature = new InstantBuild();

        feature.Apply(Context(memory, 1000));
        Check.True(feature.Failure is not null && feature.Failure.Contains(sites[1].Name), $"failure names the site: {feature.Failure}");
        Bytes(Convert.ToHexString(sites[0].OriginalBytes), memory.Peek(sites[0].Site, 5), "first site rolled back");

        int writes = memory.Writes.Count;
        feature.Apply(Context(memory, 1001));
        Check.Equal(writes, memory.Writes.Count, "a failed feature is not retried every tick");

        feature.Remove(memory);
        Check.True(feature.Failure is null, "Remove clears the failure so the next arm retries");
    }

    public static void TestUnlimitedPowerClearsTheLocalSabotageTimer()
    {
        var memory = MemoryWithOriginals(UnlimitedPower.CreatePatches(Builds.Value[0].Addresses));
        uint sabotageField = LocalPlayer + GameAddresses.PlayerEnergySabotageFrame;
        memory.MapUInt32(sabotageField, 5000);
        new UnlimitedPower().Apply(Context(memory, 1000));
        Check.Equal(0u, ReadUInt32(memory, sabotageField), "sabotage frame zeroed");
    }

    public static void TestCrashFixInstallsWithoutArmingAndFollowsANewProcess()
    {
        var feature = new CrashFix();
        Check.True(!feature.NeedsArming && feature.Enabled, "crash fix is on and needs no arming");
        var site = CrashFix.CreatePatches(Builds.Value[0].Addresses)[0];
        var first = MemoryWithOriginals([site]);
        feature.Apply(Context(first, 0));
        Check.Equal((byte)0xE9, first.Peek(site.Site, 1)[0], "hooked in the first process");
        Check.Equal((byte)0, first.Peek(GuardOf(first, site.Site).Armed, 1)[0], "never armed");

        var restarted = MemoryWithOriginals([site]);
        feature.Apply(Context(restarted, 0));
        Check.Equal((byte)0xE9, restarted.Peek(site.Site, 1)[0], "hooked again after a game restart");
    }

    /// <summary>Injects what FakeMemory cannot: torn writes, death right after a write, a failing suspend.</summary>
    private sealed class FaultyMemory(FakeMemory inner) : IProcessMemory
    {
        public uint? TearJumpsAt { get; init; }
        public uint? DieAfterWriteTo { get; init; }
        public bool FailSuspend { get; init; }

        public bool IsAlive => inner.IsAlive;

        public bool TryRead(uint address, Span<byte> buffer) => inner.TryRead(address, buffer);

        public bool TryWrite(uint address, ReadOnlySpan<byte> data)
        {
            bool isTorn = address == TearJumpsAt && data[0] == 0xE9;
            bool written = inner.TryWrite(address, isTorn ? data[..1] : data);
            if (address == DieAfterWriteTo)
                inner.IsAlive = false;
            return written;
        }

        public uint Allocate(int size) => inner.Allocate(size);

        public bool Protect(uint address, int size, PageAccess access) => inner.Protect(address, size, access);

        public void SuspendThreads()
        {
            if (FailSuspend)
                throw new InvalidOperationException("NtSuspendProcess failed (NTSTATUS 0xC0000022)");
            inner.SuspendThreads();
        }

        public void ResumeThreads() => inner.ResumeThreads();

        public bool FlushCode(uint address, int size) => inner.FlushCode(address, size);
    }

    // Reviewed with objdump -D -b binary -m i386 -M intel --adjust-vma=0x30001000 (guard page 0x30000000).
    private const string ReviewedCalcTimeToBuildCave =
        "8B 05 64 BB A2 00 85 C0 74 4A"                // mov eax,[TheGameLogic]; test; je fall-through
        + "8B 90 94 00 00 00 85 D2 74 05 83 FA 02 75 3B" // mode: 0, or 2, else fall-through
        + "80 3D 00 00 00 30 00 74 32"                  // armed byte
        + "8B 50 3C 3B 15 04 00 00 30 72 27"            // frame >= start
        + "3B 15 08 00 00 30 73 1F"                     // frame < expiry
        + "8B 15 00 B6 A2 00 85 D2 74 15 8B 52 0C 85 D2 74 0E" // edx = m_local, non-NULL
        + "3B 54 24 04 75 08 B8 01 00 00 00 C2 04 00"   // player == local: return 1 frame
        + "83 EC 08 53 56 E9 47 B8 58 D0";              // stolen prologue; jmp 0x58C8A5

    private const string ReviewedCrashFixCave =
        "8B 54 24 18 85 D2 0F 84 0B 3E 5D D0"          // unit NULL: je 0x5D4E17 (pop x5; ret 8)
        + "85 C0 8B 54 24 18 8B 4A 64 E9 0C 3C 5D D0";  // test eax,eax; stolen movs; jmp 0x5D4C26
}
