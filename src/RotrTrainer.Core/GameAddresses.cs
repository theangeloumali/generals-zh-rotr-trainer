namespace RotrTrainer.Core;

/// <summary>Per-build virtual addresses, resolved by signature. Offsets shared by every 1.04 build are constants.</summary>
public sealed record GameAddresses
{
    public required string BuildName { get; init; }

    // Globals (address of the pointer variable, not its value)
    public required uint PlayerListPointer { get; init; }
    public required uint GameLogicPointer { get; init; }

    // Functions the code patches hook
    public required uint CalcTimeToBuild { get; init; }
    public required uint UpgradeCalcTimeToBuild { get; init; }
    public required uint StartPowerRecharge { get; init; }
    public required uint OnPowerBrownOutChange { get; init; }
    public required uint MoveAwayFromUnitNullSite { get; init; }
    public required uint SpecialPowerCtorRechargeSite { get; init; }
    public required uint PlayerUpdateBrownOutSite { get; init; }
    public required uint GetEnergySupplyRatio { get; init; }

    // Code the caves call or jump to
    public required uint MoveAwayFromUnitExit { get; init; }
    public required uint GetControllingPlayer { get; init; }
    public required uint SpecialPowerCtorRechargeSkip { get; init; }

    // The 30.0f operand inside UpgradeTemplate::calcTimeToBuild's stolen fmul
    public required uint UpgradeBuildTimeConstant { get; init; }

    public const uint PlayerListLocalPlayer = 0x0C;
    // Handicap floats: build time is and cost are multiplied by these (proven at Handicap::getHandicap, 0x577920)
    public const uint PlayerBuildCostHandicapUnits = 0x0C;
    public const uint PlayerBuildCostHandicapBuildings = 0x10;
    public const uint PlayerBuildTimeHandicapUnits = 0x14;
    public const uint PlayerBuildTimeHandicapBuildings = 0x18;
    public const uint PlayerType = 0x30;
    public const uint PlayerMoney = 0x38;
    public const uint PlayerEnergyProduction = 0x84;
    public const uint PlayerEnergySabotageFrame = 0x8C;
    public const uint PlayerRankLevel = 0x188;
    public const uint PlayerSkillPoints = 0x18C;
    public const uint PlayerSciencePurchasePoints = 0x190;
    public const uint PlayerLevelUpThreshold = 0x194;
    public const uint PlayerIsObserver = 0x262;
    // Pointer to the sentinel node of the shared special-power timer list (proven at 0x456ADF)
    public const uint PlayerSharedPowerTimerList = 0x418;
    public const uint PlayerIsDead = 0x448;
    public const uint EnergyOwnerPlayer = 0x10;
    public const uint GameLogicFrame = 0x3C;
    public const uint GameLogicGameMode = 0x94;

    // Object lookup vector (ID -> Object*, entries may be NULL), proven at the findObjectByID inline near 0x4A16A0
    public const uint GameLogicObjectVectorBegin = 0x70;
    public const uint GameLogicObjectVectorEnd = 0x74;

    // Object -> owning player is [[[object + Team] + Prototype] + Owner] (Object::getControllingPlayer, 0x547C30)
    public const uint ObjectTeam = 0x1B0;
    public const uint TeamPrototype = 0x08;
    public const uint TeamPrototypeOwner = 0x0C;

    // NULL-terminated BehaviorModule* array; each module's interface vtable holds getSpecialPower at slot 7
    public const uint ObjectBehaviors = 0x16C;
    public const uint ModuleInterfaceVtable = 0x10;
    public const uint InterfaceGetSpecialPowerSlot = 0x1C;
    public const uint SpecialPowerAvailableOnFrame = 0x18;
}

public enum GameMode
{
    SinglePlayer = 0,
    Lan = 1,
    Skirmish = 2,
    Replay = 3,
    Shell = 4,
    Internet = 5,
}
