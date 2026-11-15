using RotrTrainer.Core;
using RotrTrainer.Core.Features;

namespace RotrTrainer.Tests;

public static class ValueFeatureTests
{
    private const string ModdedExe =
        @"C:\Program Files (x86)\Steam\steamapps\common\Command & Conquer Generals - Zero Hour\modded.exe";

    private const uint GameLogicObject = 0x20000000;
    private const uint PlayerListObject = 0x21000000;
    private const uint LocalPlayer = 0x22000000;
    private const uint OtherPlayer = 0x23000000;

    // Covers every Player field the trainer touches (the last is the m_isPlayerDead byte at +0x448).
    private const int PlayerRecordSize = 0x44C;

    // Engine tests resolve the real allow-listed build so fake memory sits at its true addresses.
    private static readonly Lazy<GameAddresses> Modded = new(() => BuildCatalog.Resolve(ModdedExe).Addresses!);

    private static FakeMemory Skirmish()
    {
        var addresses = Modded.Value;
        var memory = new FakeMemory();
        memory.MapUInt32(addresses.GameLogicPointer, GameLogicObject);
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicFrame, 100);
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicGameMode, (uint)GameMode.Skirmish);
        memory.MapUInt32(addresses.PlayerListPointer, PlayerListObject);
        memory.MapUInt32(PlayerListObject + GameAddresses.PlayerListLocalPlayer, LocalPlayer);
        memory.Map(addresses.ImageCheckAddress, addresses.ImageCheckBytes);
        MapPlayer(memory, LocalPlayer);
        MapPlayer(memory, OtherPlayer);
        return memory;
    }

    private static void MapPlayer(FakeMemory memory, uint player)
    {
        memory.Map(player, new byte[PlayerRecordSize]);
        memory.MapUInt32(player + GameAddresses.PlayerMoney, 500);
        memory.MapUInt32(player + GameAddresses.PlayerRankLevel, 1);
        memory.MapUInt32(player + GameAddresses.PlayerSkillPoints, 100);
        memory.MapUInt32(player + GameAddresses.PlayerLevelUpThreshold, 400);
    }

    private static uint Read(FakeMemory memory, uint address) => BitConverter.ToUInt32(memory.Peek(address, 4));

    private static void SetMode(FakeMemory memory, GameMode mode) =>
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicGameMode, (uint)mode);

    // ---- Guards: local player flags ----

    private static GuardVerdict Evaluate(FakeMemory memory) => Guards.Evaluate(memory, Modded.Value, () => false);

    public static void TestGuardsAllowALiveHumanLocalPlayer()
    {
        Check.True(Evaluate(Skirmish()).MayArm, "live human player may arm");
    }

    public static void TestGuardsRefuseObserverLocalPlayer()
    {
        var memory = Skirmish();
        memory.Map(LocalPlayer + GameAddresses.PlayerIsObserver, [1]);
        var verdict = Evaluate(memory);
        Check.True(!verdict.MayArm && verdict.Reason.Contains("observer"), verdict.Reason);
    }

    public static void TestGuardsRefuseDeadLocalPlayer()
    {
        var memory = Skirmish();
        memory.Map(LocalPlayer + GameAddresses.PlayerIsDead, [1]);
        var verdict = Evaluate(memory);
        Check.True(!verdict.MayArm && verdict.Reason.Contains("defeated"), verdict.Reason);
    }

    public static void TestGuardsRefuseComputerLocalPlayer()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerType, 1);
        var verdict = Evaluate(memory);
        Check.True(!verdict.MayArm && verdict.Reason.Contains("human"), verdict.Reason);
    }

    public static void TestGuardsRefuseUnreadableLocalPlayerRecord()
    {
        var memory = Skirmish();
        memory.MapUInt32(PlayerListObject + GameAddresses.PlayerListLocalPlayer, 0x7F000000);
        var verdict = Evaluate(memory);
        Check.True(!verdict.MayArm && verdict.Reason.Contains("read"), verdict.Reason);
    }

    // ---- Value features, applied directly ----

    private static GameContext Context(FakeMemory memory) => new(memory, Modded.Value, LocalPlayer, 100);

    public static void TestMoneyBelowFloorIsRaised()
    {
        var memory = Skirmish();
        new MoneyFloor().Apply(Context(memory));
        Check.Equal(MoneyFloor.Floor, Read(memory, LocalPlayer + GameAddresses.PlayerMoney), "money");
        Check.Equal(1, memory.Writes.Count, "one write");
    }

    public static void TestMoneyAboveFloorIsUntouched()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerMoney, MoneyFloor.Floor + 5);
        new MoneyFloor().Apply(Context(memory));
        Check.Equal(MoneyFloor.Floor + 5, Read(memory, LocalPlayer + GameAddresses.PlayerMoney), "money");
        Check.Equal(0, memory.Writes.Count, "no write");
    }

    public static void TestMoneyFloorRemoveLeavesTheMoney()
    {
        var memory = Skirmish();
        var feature = new MoneyFloor();
        feature.Apply(Context(memory));
        feature.Remove(memory);
        Check.Equal(MoneyFloor.Floor, Read(memory, LocalPlayer + GameAddresses.PlayerMoney), "money stays");
        Check.Equal(1, memory.Writes.Count, "remove writes nothing");
    }

    private const uint NormalHandicap = 0x3F800000; // 1.0f
    private static readonly uint FastScale = BitConverter.SingleToUInt32Bits(FastBuild.BuildTimeScale);

    private static FakeMemory SkirmishWithHandicaps()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerBuildTimeHandicapUnits, NormalHandicap);
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerBuildTimeHandicapBuildings, NormalHandicap);
        return memory;
    }

    public static void TestFastBuildScalesBothBuildTimeHandicaps()
    {
        var memory = SkirmishWithHandicaps();
        new FastBuild().Apply(Context(memory));
        Check.Equal(FastScale, Read(memory, LocalPlayer + GameAddresses.PlayerBuildTimeHandicapUnits), "units");
        Check.Equal(FastScale, Read(memory, LocalPlayer + GameAddresses.PlayerBuildTimeHandicapBuildings), "buildings");
    }

    public static void TestFastBuildWritesOnlyOnceWhileScaled()
    {
        var memory = SkirmishWithHandicaps();
        var feature = new FastBuild();
        feature.Apply(Context(memory));
        feature.Apply(Context(memory));
        Check.Equal(2, memory.Writes.Count, "two handicap writes in total");
    }

    public static void TestFastBuildRemoveRestoresTheOriginalHandicaps()
    {
        var memory = SkirmishWithHandicaps();
        var feature = new FastBuild();
        feature.Apply(Context(memory));
        feature.Remove(memory);
        Check.Equal(NormalHandicap, Read(memory, LocalPlayer + GameAddresses.PlayerBuildTimeHandicapUnits), "units restored");
        Check.Equal(NormalHandicap, Read(memory, LocalPlayer + GameAddresses.PlayerBuildTimeHandicapBuildings), "buildings restored");
    }

    public static void TestFastBuildRemoveSkipsMemoryThatNoLongerHoldsItsValue()
    {
        var memory = SkirmishWithHandicaps();
        var feature = new FastBuild();
        feature.Apply(Context(memory));
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerBuildTimeHandicapUnits, 0xDEADBEEF);
        feature.Remove(memory);
        Check.Equal(0xDEADBEEF, Read(memory, LocalPlayer + GameAddresses.PlayerBuildTimeHandicapUnits), "a value that is no longer ours is left alone");
    }

    public static void TestFastBuildRemoveWithoutApplyWritesNothing()
    {
        var memory = SkirmishWithHandicaps();
        new FastBuild().Remove(memory);
        Check.Equal(0, memory.Writes.Count, "nothing to undo");
    }

    public static void TestFreeBuildScalesBothBuildCostHandicaps()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerBuildCostHandicapUnits, NormalHandicap);
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerBuildCostHandicapBuildings, NormalHandicap);
        var feature = new FreeBuild();
        feature.Apply(Context(memory));
        uint costScale = BitConverter.SingleToUInt32Bits(FreeBuild.BuildCostScale);
        Check.Equal(costScale, Read(memory, LocalPlayer + GameAddresses.PlayerBuildCostHandicapUnits), "units");
        Check.Equal(costScale, Read(memory, LocalPlayer + GameAddresses.PlayerBuildCostHandicapBuildings), "buildings");
        feature.Remove(memory);
        Check.Equal(NormalHandicap, Read(memory, LocalPlayer + GameAddresses.PlayerBuildCostHandicapUnits), "restored");
    }

    public static void TestPowerBonusIsAddedOnce()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerEnergyProduction, 30);
        var feature = new PowerBonus();
        feature.Apply(Context(memory));
        feature.Apply(Context(memory));
        Check.Equal(30 + PowerBonus.Bonus, Read(memory, LocalPlayer + GameAddresses.PlayerEnergyProduction), "production");
    }

    public static void TestPowerBonusIsAddedAgainAfterTheGameResetsProduction()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerEnergyProduction, 30);
        var feature = new PowerBonus();
        feature.Apply(Context(memory));
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerEnergyProduction, 10);
        feature.Apply(Context(memory));
        Check.Equal(10 + PowerBonus.Bonus, Read(memory, LocalPlayer + GameAddresses.PlayerEnergyProduction), "re-added");
    }

    public static void TestPowerBonusExpiresTheSabotageTimerSoTheGameLiftsItself()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerEnergyProduction, 30);
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerEnergySabotageFrame, 9000);
        new PowerBonus().Apply(Context(memory));
        Check.Equal(PowerBonus.ExpiredSabotageFrame, Read(memory, LocalPlayer + GameAddresses.PlayerEnergySabotageFrame), "expired, not zeroed");
    }

    public static void TestPowerBonusRemoveSubtractsTheBonus()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerEnergyProduction, 30);
        var feature = new PowerBonus();
        feature.Apply(Context(memory));
        feature.Remove(memory);
        Check.Equal(30u, Read(memory, LocalPlayer + GameAddresses.PlayerEnergyProduction), "back to real production");
    }

    public static void TestPowerBonusRemoveNeverDrivesProductionNegative()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerEnergyProduction, 30);
        var feature = new PowerBonus();
        feature.Apply(Context(memory));
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerEnergyProduction, 10);
        int writesBefore = memory.Writes.Count;
        feature.Remove(memory);
        Check.Equal(writesBefore, memory.Writes.Count, "a reset record no longer holds the bonus");
    }

    private const uint TimerListHead = 0x00700000;
    private const uint FirstTimerNode = 0x00700100;
    private const uint SecondTimerNode = 0x00700200;

    // STLport list: the player holds a pointer to a sentinel node; nodes are next, prev, templateId, readyFrame.
    private static FakeMemory SkirmishWithSharedTimers(uint firstReadyFrame, uint secondReadyFrame)
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerSharedPowerTimerList, TimerListHead);
        memory.MapUInt32(TimerListHead, FirstTimerNode);
        memory.MapUInt32(FirstTimerNode, SecondTimerNode);
        memory.MapUInt32(FirstTimerNode + SharedPowerCooldown.NodeReadyFrame, firstReadyFrame);
        memory.MapUInt32(SecondTimerNode, TimerListHead);
        memory.MapUInt32(SecondTimerNode + SharedPowerCooldown.NodeReadyFrame, secondReadyFrame);
        return memory;
    }

    public static void TestSharedPowerTimersInTheFutureAreMadeReadyNow()
    {
        var context = Context(SkirmishWithSharedTimers(firstReadyFrame: 90_000, secondReadyFrame: 1));
        var memory = (FakeMemory)context.Memory;
        new SharedPowerCooldown().Apply(context);
        Check.Equal(context.Frame, Read(memory, FirstTimerNode + SharedPowerCooldown.NodeReadyFrame), "future timer");
        Check.Equal(1u, Read(memory, SecondTimerNode + SharedPowerCooldown.NodeReadyFrame), "already ready, untouched");
        Check.Equal(1, memory.Writes.Count, "one write");
    }

    public static void TestSharedPowerTimerWalkStopsOnACorruptList()
    {
        var context = Context(SkirmishWithSharedTimers(firstReadyFrame: 90_000, secondReadyFrame: 90_000));
        var memory = (FakeMemory)context.Memory;
        memory.MapUInt32(SecondTimerNode, FirstTimerNode);
        new SharedPowerCooldown().Apply(context);
        Check.True(memory.Writes.Count <= SharedPowerCooldown.MaxNodes, "a looping list is abandoned");
    }

    public static void TestSharedPowerTimerEmptyListWritesNothing()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerSharedPowerTimerList, TimerListHead);
        memory.MapUInt32(TimerListHead, TimerListHead);
        new SharedPowerCooldown().Apply(Context(memory));
        Check.Equal(0, memory.Writes.Count, "nothing to do");
    }

    private const uint ObjectVector = 0x00800000;
    private const uint SpecialPowerVtable = 0x00950000;
    private const uint SpecialPowerAccessor = 0x00545000;
    private const uint OtherVtable = 0x00951000;
    private const uint NullAccessor = 0x008E0000;

    // One object owned by `owner`, with a plain module followed by a special-power module charging until `readyFrame`.
    private static uint MapObjectWithSuperweapon(FakeMemory memory, uint objectAddress, uint owner, uint readyFrame)
    {
        uint team = objectAddress + 0x1000, prototype = objectAddress + 0x1100, behaviors = objectAddress + 0x1200;
        uint plainModule = objectAddress + 0x1300, powerModule = objectAddress + 0x1400;
        memory.MapUInt32(objectAddress + GameAddresses.ObjectTeam, team);
        memory.MapUInt32(team + GameAddresses.TeamPrototype, prototype);
        memory.MapUInt32(prototype + GameAddresses.TeamPrototypeOwner, owner);
        memory.MapUInt32(objectAddress + GameAddresses.ObjectBehaviors, behaviors);
        memory.MapUInt32(behaviors, plainModule);
        memory.MapUInt32(behaviors + 4, powerModule);
        memory.MapUInt32(behaviors + 8, 0);
        memory.MapUInt32(plainModule + GameAddresses.ModuleInterfaceVtable, OtherVtable);
        memory.MapUInt32(powerModule + GameAddresses.ModuleInterfaceVtable, SpecialPowerVtable);
        memory.MapUInt32(powerModule + GameAddresses.SpecialPowerAvailableOnFrame, readyFrame);
        return powerModule + GameAddresses.SpecialPowerAvailableOnFrame;
    }

    private static FakeMemory SkirmishWithObjects(params uint[] objects)
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerSharedPowerTimerList, TimerListHead);
        memory.MapUInt32(TimerListHead, TimerListHead);
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicObjectVectorBegin, ObjectVector);
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicObjectVectorEnd, ObjectVector + 4 * (uint)objects.Length);
        for (int index = 0; index < objects.Length; index++)
            memory.MapUInt32(ObjectVector + 4 * (uint)index, objects[index]);
        memory.MapUInt32(SpecialPowerVtable + GameAddresses.InterfaceGetSpecialPowerSlot, SpecialPowerAccessor);
        memory.Map(SpecialPowerAccessor, SharedPowerCooldown.SpecialPowerAccessorCode);
        memory.MapUInt32(OtherVtable + GameAddresses.InterfaceGetSpecialPowerSlot, NullAccessor);
        memory.Map(NullAccessor, [0x33, 0xC0, 0xC3, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90]);
        return memory;
    }

    public static void TestBuildingPowerTimerOfTheLocalPlayerIsMadeReady()
    {
        var memory = SkirmishWithObjects(0, 0x00A00000);
        uint timer = MapObjectWithSuperweapon(memory, 0x00A00000, LocalPlayer, readyFrame: 9_000);
        new SharedPowerCooldown().Apply(Context(memory));
        Check.Equal(100u, Read(memory, timer), "ready at the current frame");
    }

    public static void TestBuildingPowerTimerFarIntoAHugeObjectTableIsStillReached()
    {
        // Late in a long match the table holds hundreds of thousands of mostly empty entries.
        const int tableEntries = 200_000;
        const int superweaponIndex = 150_000;
        var memory = SkirmishWithObjects();
        memory.Map(ObjectVector, new byte[4 * tableEntries]);
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicObjectVectorEnd, ObjectVector + 4 * tableEntries);
        memory.MapUInt32(ObjectVector + 4 * superweaponIndex, 0x00A00000);
        uint timer = MapObjectWithSuperweapon(memory, 0x00A00000, LocalPlayer, readyFrame: 9_000);

        var feature = new SharedPowerCooldown();
        for (int tick = 0; tick < 30; tick++)
            feature.Apply(Context(memory));
        Check.Equal(100u, Read(memory, timer), "reached within three seconds of ticks");
    }

    // Links objects into the game's live-object list and registers each in the ID lookup table.
    private static void LinkLiveObjects(FakeMemory memory, params (uint Address, uint Id)[] objects)
    {
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicFirstObject, objects.Length > 0 ? objects[0].Address : 0);
        for (int index = 0; index < objects.Length; index++)
        {
            var (address, id) = objects[index];
            memory.MapUInt32(address + GameAddresses.ObjectId, id);
            memory.MapUInt32(address + GameAddresses.ObjectNext, index + 1 < objects.Length ? objects[index + 1].Address : 0);
            memory.MapUInt32(ObjectVector + 4 * id, address);
        }
    }

    public static void TestSuperweaponBuiltLateInALongMatchIsReached()
    {
        // IDs are never reused: after hours of play a new building's ID is in the millions.
        const uint lateId = 3_000_000;
        var memory = SkirmishWithObjects();
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicObjectVectorEnd, ObjectVector + 4 * 4_194_304);
        uint timer = MapObjectWithSuperweapon(memory, 0x00A00000, LocalPlayer, readyFrame: 9_000);
        LinkLiveObjects(memory, (0x00A00000, lateId));

        new SharedPowerCooldown().Apply(Context(memory));
        Check.Equal(100u, Read(memory, timer), "reached through the live-object list");
    }

    public static void TestLiveObjectListThatDoesNotMatchTheLookupTableIsNotTrusted()
    {
        var memory = SkirmishWithObjects(0x00A00000);
        uint timer = MapObjectWithSuperweapon(memory, 0x00A00000, LocalPlayer, readyFrame: 9_000);
        // A "list" whose head is not what the lookup table holds for its ID: the offsets cannot be trusted.
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicFirstObject, 0x00C00000);
        memory.MapUInt32(0x00C00000 + GameAddresses.ObjectId, 0);
        memory.MapUInt32(0x00C00000 + GameAddresses.ObjectNext, 0);

        new SharedPowerCooldown().Apply(Context(memory));
        Check.Equal(100u, Read(memory, timer), "falls back to scanning the lookup table");
    }

    public static void TestNoCooldownReportsWhatItFound()
    {
        var memory = SkirmishWithObjects(0x00A00000, 0x00B00000);
        MapObjectWithSuperweapon(memory, 0x00A00000, LocalPlayer, readyFrame: 9_000);
        MapObjectWithSuperweapon(memory, 0x00B00000, OtherPlayer, readyFrame: 9_000);
        var feature = new SharedPowerCooldown();
        Check.Equal("General powers and superweapons", feature.Description, "before any scan");
        feature.Apply(Context(memory));
        Check.Equal("1 of your units and buildings seen, 1 with a power", feature.Description, "after a full scan");
    }

    public static void TestBuildingPowerTimerOfAnotherPlayerIsLeftAlone()
    {
        var memory = SkirmishWithObjects(0x00A00000);
        uint timer = MapObjectWithSuperweapon(memory, 0x00A00000, OtherPlayer, readyFrame: 9_000);
        new SharedPowerCooldown().Apply(Context(memory));
        Check.Equal(9_000u, Read(memory, timer), "AI superweapon keeps charging");
        Check.Equal(0, memory.Writes.Count, "no write");
    }

    public static void TestBuildingPowerTimerThatLooksLikeGarbageIsLeftAlone()
    {
        var memory = SkirmishWithObjects(0x00A00000);
        uint timer = MapObjectWithSuperweapon(memory, 0x00A00000, LocalPlayer, readyFrame: 0xFFFFFFFF);
        new SharedPowerCooldown().Apply(Context(memory));
        Check.Equal(0xFFFFFFFFu, Read(memory, timer), "not a plausible ready frame");
    }

    public static void TestSciencePointsAreKeptAtTheFloor()
    {
        var memory = Skirmish();
        new GeneralPoints().Apply(Context(memory));
        Check.Equal(GeneralPoints.SciencePointsFloor,
            Read(memory, LocalPlayer + GameAddresses.PlayerSciencePurchasePoints), "raised to floor");

        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerSciencePurchasePoints, GeneralPoints.SciencePointsFloor + 3);
        new GeneralPoints().Apply(Context(memory));
        Check.Equal(GeneralPoints.SciencePointsFloor + 3,
            Read(memory, LocalPlayer + GameAddresses.PlayerSciencePurchasePoints), "above floor untouched");
    }

    public static void TestSkillPointsAreSetToTheLevelUpThreshold()
    {
        var memory = Skirmish();
        new GeneralPoints().Apply(Context(memory));
        Check.Equal(400u, Read(memory, LocalPlayer + GameAddresses.PlayerSkillPoints), "skill = threshold");
        Check.Equal(1u, Read(memory, LocalPlayer + GameAddresses.PlayerRankLevel), "rank is never written");
    }

    public static void TestSkillPointsIgnoreAnImplausibleThreshold()
    {
        var memory = Skirmish();
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerLevelUpThreshold, int.MaxValue);
        new GeneralPoints().Apply(Context(memory));
        Check.Equal(100u, Read(memory, LocalPlayer + GameAddresses.PlayerSkillPoints), "skill untouched");
    }

    public static void TestValueFeaturesWriteNothingWhenReadsFail()
    {
        var memory = Skirmish();
        memory.FailReads = true;
        new MoneyFloor().Apply(Context(memory));
        new GeneralPoints().Apply(Context(memory));
        Check.Equal(0, memory.Writes.Count, "no blind writes");
    }

    // ---- TrainerEngine ----

    private static readonly DateTime Started = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);

    private sealed class GameSlot
    {
        public AttachedGame? Game { get; set; }
        public int Attaches { get; set; }
    }

    private sealed class ProbeFeature(bool needsArming) : IFeature
    {
        public string Name => needsArming ? "armed probe" : "always probe";
        public string Description => "test double";
        public bool Enabled { get; set; } = true;
        public bool NeedsArming => needsArming;
        public int Applies { get; private set; }
        public int Removes { get; private set; }
        public Action? DuringApply { get; set; }

        public void Apply(GameContext context)
        {
            Applies++;
            DuringApply?.Invoke();
        }

        public void Remove(IProcessMemory memory) => Removes++;
    }

    public static void TestStatusSaysArmingIsAllowedInASkirmish()
    {
        var (engine, _) = Engine(Skirmish(), [new MoneyFloor()]);
        engine.Tick();
        Check.True(engine.Status.MayArm, "skirmish allows arming");
    }

    public static void TestStatusSaysArmingIsNotAllowedAtTheMenu()
    {
        var memory = Skirmish();
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicGameMode, (uint)GameMode.Shell);
        var (engine, _) = Engine(memory, [new MoneyFloor()]);
        engine.Tick();
        Check.True(!engine.Status.MayArm, "menu does not allow arming");
    }

    public static void TestStatusReportsGenToolWhenItsProxyDllSitsBesideTheGame()
    {
        var (engine, _) = Engine(Skirmish(), [new MoneyFloor()]);
        engine.Tick();
        Check.True(engine.Status.GenToolPresent, "d3d8.dll is in the game folder on this machine");
    }

    public static void TestStatusCarriesEachFeaturesDescription()
    {
        var (engine, _) = Engine(Skirmish(), [new MoneyFloor()]);
        engine.Tick();
        Check.Equal(new MoneyFloor().Description, engine.Status.Features[0].Description, "description");
        Check.True(engine.Status.Features[0].Description.Length > 0, "not empty");
    }

    private static (TrainerEngine Engine, GameSlot Slot) Engine(
        FakeMemory memory, IReadOnlyList<IFeature> features, string exePath = ModdedExe, bool? ownsNetwork = false)
    {
        var slot = new GameSlot { Game = new AttachedGame(memory, exePath, 4242, Started) };
        GameAttacher attach = (out string failure) =>
        {
            slot.Attaches++;
            failure = slot.Game is null ? "game is not running" : "";
            return slot.Game;
        };
        return (new TrainerEngine(features, attach, _ => ownsNetwork), slot);
    }

    private static TrainerEngine ArmedEngine(FakeMemory memory, params IFeature[] features)
    {
        var (engine, _) = Engine(memory, features);
        engine.Tick();
        Check.True(engine.Arm(), $"arms in skirmish ({engine.Status.GuardReason})");
        return engine;
    }

    public static void TestEngineStartsDisarmedAndStaysDisarmedOnAttach()
    {
        var memory = Skirmish();
        var (engine, _) = Engine(memory, [new MoneyFloor { Enabled = true }]);
        Check.True(!engine.Status.IsAttached && !engine.Status.IsArmed, "nothing before the first tick");
        engine.Tick();
        Check.True(engine.Status.IsAttached, "attached");
        Check.True(engine.Status.BuildName!.Contains("modded.exe"), $"build {engine.Status.BuildName}");
        Check.True(!engine.Status.IsArmed, "disarmed after attach");
        Check.Equal(0, memory.Writes.Count, "no writes");
    }

    public static void TestArmedMoneyFloorWritesTheFloor()
    {
        var memory = Skirmish();
        var engine = ArmedEngine(memory, new MoneyFloor { Enabled = true });
        engine.Tick();
        Check.Equal(MoneyFloor.Floor, Read(memory, LocalPlayer + GameAddresses.PlayerMoney), "money");
    }

    public static void TestArmFailsWhileGuardsFail()
    {
        foreach (var mode in new[] { GameMode.Shell, GameMode.Lan, GameMode.Replay, GameMode.Internet })
        {
            var memory = Skirmish();
            SetMode(memory, mode);
            var (engine, _) = Engine(memory, []);
            engine.Tick();
            Check.True(!engine.Arm(), $"{mode} refuses");
            Check.True(engine.Status.GuardReason.Contains(mode.ToString()), engine.Status.GuardReason);
        }

        var nullPlayer = Skirmish();
        nullPlayer.MapUInt32(PlayerListObject + GameAddresses.PlayerListLocalPlayer, 0);
        var (nullPlayerEngine, _) = Engine(nullPlayer, []);
        nullPlayerEngine.Tick();
        Check.True(!nullPlayerEngine.Arm(), "null local player refuses");

        var (networkEngine, _) = Engine(Skirmish(), [], ownsNetwork: true);
        networkEngine.Tick();
        Check.True(!networkEngine.Arm(), "network endpoints refuse");
    }

    public static void TestObserverAndDefeatedPlayersRefuseToArm()
    {
        foreach (uint flag in new[] { GameAddresses.PlayerIsObserver, GameAddresses.PlayerIsDead })
        {
            var memory = Skirmish();
            memory.Map(LocalPlayer + flag, [1]);
            var (engine, _) = Engine(memory, [new MoneyFloor { Enabled = true }]);
            engine.Tick();
            Check.True(!engine.Arm(), $"flag 0x{flag:X} refuses");
            engine.Tick();
            Check.Equal(0, memory.Writes.Count, "no writes");
        }
    }

    public static void TestArmChecksGuardsAtCallTime()
    {
        var memory = Skirmish();
        var (engine, _) = Engine(memory, []);
        engine.Tick();
        SetMode(memory, GameMode.Lan);
        Check.True(!engine.Arm(), "LAN since the last tick refuses");
    }

    public static void TestFeaturesDoNotWriteWhileDisarmed()
    {
        var memory = Skirmish();
        var probe = new ProbeFeature(needsArming: true);
        var (engine, _) = Engine(memory, [new MoneyFloor { Enabled = true }, new GeneralPoints { Enabled = true }, probe]);
        for (int tick = 0; tick < 3; tick++)
            engine.Tick();
        Check.Equal(0, memory.Writes.Count, "no writes");
        Check.Equal(0, probe.Applies, "armed feature never applied");
    }

    public static void TestDisabledFeatureWritesNothingWhileArmed()
    {
        var memory = Skirmish();
        var engine = ArmedEngine(memory, new MoneyFloor { Enabled = false });
        engine.Tick();
        Check.Equal(0, memory.Writes.Count, "no writes");
    }

    public static void TestUnarmedFeatureAppliesWhileDisarmedButNeverOnUnknownBuild()
    {
        var known = new ProbeFeature(needsArming: false);
        var (engine, _) = Engine(Skirmish(), [known]);
        engine.Tick();
        Check.Equal(1, known.Applies, "applies on a known build while disarmed");

        var unknown = new ProbeFeature(needsArming: false);
        var memory = Skirmish();
        var (unknownEngine, _) = Engine(memory, [unknown, new MoneyFloor { Enabled = true }], Environment.ProcessPath!);
        unknownEngine.Tick();
        unknownEngine.Tick();
        Check.Equal(0, unknown.Applies, "never on an unknown build");
        Check.True(unknownEngine.Status.IsAttached && unknownEngine.Status.BuildName is null, "attached read-only");
        Check.True(unknownEngine.Status.Refusal!.Contains("allow-list"), unknownEngine.Status.Refusal);
        Check.True(!unknownEngine.Arm(), "cannot arm an unknown build");
        Check.Equal(0, memory.Writes.Count, "no writes");
    }

    public static void TestModeChangeToShellDisarmsAndRemoves()
    {
        var memory = Skirmish();
        var armed = new ProbeFeature(needsArming: true);
        var always = new ProbeFeature(needsArming: false);
        var engine = ArmedEngine(memory, armed, always);
        engine.Tick();
        Check.Equal(1, armed.Applies, "applied while armed");

        SetMode(memory, GameMode.Shell);
        engine.Tick();
        Check.True(!engine.Status.IsArmed, "disarmed in the shell");
        Check.Equal(1, armed.Removes, "armed feature removed");
        Check.Equal(1, always.Removes, "a fix that needs no arming still leaves a game the guards refuse");

        SetMode(memory, GameMode.Skirmish);
        engine.Tick();
        Check.True(!engine.Status.IsArmed, "a new match needs a new arm");
        Check.Equal(1, armed.Applies, "not reapplied");
    }

    public static void TestLocalPlayerChangeDisarms()
    {
        var memory = Skirmish();
        var armed = new ProbeFeature(needsArming: true);
        var engine = ArmedEngine(memory, armed, new MoneyFloor { Enabled = true });
        engine.Tick();

        memory.MapUInt32(PlayerListObject + GameAddresses.PlayerListLocalPlayer, OtherPlayer);
        engine.Tick();
        Check.True(!engine.Status.IsArmed, "new Player pointer disarms");
        Check.Equal(1, armed.Removes, "removed");
        Check.Equal(500u, Read(memory, OtherPlayer + GameAddresses.PlayerMoney), "new player untouched");
    }

    public static void TestFrameGoingBackwardsDisarms()
    {
        var memory = Skirmish();
        var engine = ArmedEngine(memory);
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicFrame, 5000);
        engine.Tick();
        Check.True(engine.Status.IsArmed, "frames moving forward keep the arm");
        memory.MapUInt32(GameLogicObject + GameAddresses.GameLogicFrame, 10);
        engine.Tick();
        Check.True(!engine.Status.IsArmed, "a restarted match disarms");
    }

    public static void TestGameRestartDisarmsAndReattaches()
    {
        var oldMemory = Skirmish();
        var armed = new ProbeFeature(needsArming: true);
        var (engine, slot) = Engine(oldMemory, [armed, new MoneyFloor { Enabled = true }]);
        engine.Tick();
        Check.True(engine.Arm(), "armed");
        engine.Tick();
        int oldWrites = oldMemory.Writes.Count;

        oldMemory.IsAlive = false;
        var newMemory = Skirmish();
        slot.Game = new AttachedGame(newMemory, ModdedExe, 4242, Started.AddMinutes(5));
        engine.Tick();
        Check.Equal(2, slot.Attaches, "re-attached");
        Check.True(engine.Status.IsAttached && !engine.Status.IsArmed, "attached to the new game, disarmed");
        Check.Equal(1, armed.Removes, "told to drop its state, even though the process is gone");
        Check.Equal(0, newMemory.Writes.Count, "nothing written before a new arm");

        Check.True(engine.Arm(), "arms the new game");
        engine.Tick();
        Check.Equal(MoneyFloor.Floor, Read(newMemory, LocalPlayer + GameAddresses.PlayerMoney), "new game money");
        Check.Equal(oldWrites, oldMemory.Writes.Count, "old game untouched");
    }

    public static void TestFastBuildIsUndoneInARestartedGame()
    {
        var oldMemory = SkirmishWithHandicaps();
        var fastBuild = new FastBuild { Enabled = true };
        var (engine, slot) = Engine(oldMemory, [fastBuild]);
        engine.Tick();
        Check.True(engine.Arm(), "armed");
        engine.Tick();

        oldMemory.IsAlive = false;
        var newMemory = SkirmishWithHandicaps();
        slot.Game = new AttachedGame(newMemory, ModdedExe, 4242, Started.AddMinutes(5));
        engine.Tick();
        Check.True(engine.Arm(), "arms the new game");
        engine.Tick();
        Check.Equal(FastScale, Read(newMemory, LocalPlayer + GameAddresses.PlayerBuildTimeHandicapUnits), "scaled in the new game");

        engine.Disarm();
        Check.Equal(NormalHandicap, Read(newMemory, LocalPlayer + GameAddresses.PlayerBuildTimeHandicapUnits), "restored on disarm");
    }

    public static void TestOneHandicapResetByTheGameStillLetsTheOtherRestore()
    {
        var memory = SkirmishWithHandicaps();
        var feature = new FastBuild();
        feature.Apply(Context(memory));
        memory.MapUInt32(LocalPlayer + GameAddresses.PlayerBuildTimeHandicapUnits, NormalHandicap);
        feature.Remove(memory);
        Check.Equal(NormalHandicap, Read(memory, LocalPlayer + GameAddresses.PlayerBuildTimeHandicapBuildings), "buildings restored");
    }

    public static void TestArmWhileArmedReportsTheCurrentGuardVerdict()
    {
        var memory = Skirmish();
        var engine = ArmedEngine(memory, new MoneyFloor());
        memory.Map(LocalPlayer + GameAddresses.PlayerIsDead, [1]);
        Check.True(!engine.Arm(), "a defeated player cannot stay armed");
        Check.True(!engine.Status.IsArmed, "disarmed");
    }

    public static void TestARunningImageThatDiffersFromTheFileIsRefused()
    {
        var memory = SkirmishWithHandicaps();
        byte[] otherCode = new byte[Modded.Value.ImageCheckBytes.Length];
        memory.Map(Modded.Value.ImageCheckAddress, otherCode);
        var (engine, _) = Engine(memory, [new FastBuild { Enabled = true }]);
        engine.Tick();
        Check.True(engine.Status.Refusal!.Contains("does not match"), engine.Status.Refusal);
        Check.True(!engine.Arm(), "cannot arm against a different image");
        Check.Equal(0, memory.Writes.Count, "nothing is written");
    }

    public static void TestProcessDeathIsAState()
    {
        var memory = Skirmish();
        var armed = new ProbeFeature(needsArming: true);
        var (engine, slot) = Engine(memory, [armed]);
        engine.Tick();
        engine.Arm();
        engine.Tick();

        memory.IsAlive = false;
        slot.Game = null;
        engine.Tick();
        Check.True(!engine.Status.IsAttached && !engine.Status.IsArmed, "detached and disarmed");
        Check.True(engine.Status.Refusal!.Contains("not running"), engine.Status.Refusal);
        Check.True(!engine.Arm(), "cannot arm without a game");
        engine.Disarm();
        engine.Dispose();
        Check.Equal(1, armed.Removes, "told once to drop its state for the dead process");
    }

    public static void TestProcessDeathMidTickIsAState()
    {
        var memory = Skirmish();
        var dying = new ProbeFeature(needsArming: false)
        {
            DuringApply = () =>
            {
                memory.IsAlive = false;
                throw new InvalidOperationException("NtSuspendProcess failed");
            },
        };
        var (engine, _) = Engine(memory, [dying]);
        engine.Tick();
        Check.True(!engine.Status.IsAttached, "detached after the process died mid-tick");
    }

    public static void TestWritesStayInsideTheLocalPlayerRecord()
    {
        var memory = Skirmish();
        var engine = ArmedEngine(memory, new MoneyFloor { Enabled = true }, new GeneralPoints { Enabled = true });
        for (int tick = 0; tick < 3; tick++)
            engine.Tick();
        Check.True(memory.Writes.Count >= 3, "money, points and skill were written");
        foreach (var write in memory.Writes)
            Check.True(write.Address >= LocalPlayer && write.Address + write.Data.Length <= LocalPlayer + PlayerRecordSize,
                $"write at 0x{write.Address:X8} is inside the local player");
    }

    public static void TestStatusReportsValuesAndFeatures()
    {
        var (engine, _) = Engine(Skirmish(), [new MoneyFloor { Enabled = true }, new ProbeFeature(needsArming: false)]);
        engine.Tick();
        var status = engine.Status;
        Check.Equal<uint?>(500, status.Money, "money");
        Check.Equal<uint?>(0, status.SciencePurchasePoints, "points");
        Check.Equal<uint?>(1, status.RankLevel, "rank");
        Check.Equal(2, status.Features.Count, "feature count");
        Check.Equal(new FeatureStatus("Money", new MoneyFloor().Description, true, true), status.Features[0], "money feature");
        Check.Equal(new FeatureStatus("always probe", "test double", true, false), status.Features[1], "probe feature");
    }

    public static void TestDisablingAnAppliedFeatureRemovesItOnce()
    {
        var always = new ProbeFeature(needsArming: false);
        var (engine, _) = Engine(Skirmish(), [always]);
        engine.Tick();
        always.Enabled = false;
        engine.Tick();
        engine.Tick();
        Check.Equal(1, always.Removes, "removed once");
    }

    public static void TestDisarmAndDisposeRemoveFeatures()
    {
        var armed = new ProbeFeature(needsArming: true);
        var always = new ProbeFeature(needsArming: false);
        var engine = ArmedEngine(Skirmish(), armed, always);
        engine.Tick();
        engine.Disarm();
        Check.Equal(1, armed.Removes, "disarm removes armed features");
        Check.Equal(0, always.Removes, "disarm keeps unarmed features");
        engine.Dispose();
        Check.Equal(1, armed.Removes, "not removed twice");
        Check.Equal(1, always.Removes, "dispose removes everything left");
    }
}
