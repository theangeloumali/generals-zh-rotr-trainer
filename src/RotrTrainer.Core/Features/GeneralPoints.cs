namespace RotrTrainer.Core.Features;

/// <summary>
/// Keeps general's points at a floor and skill points at the level-up threshold, so the next kill promotes
/// through the game's own addSkillPoints loop. Data writes only: rank is never written directly.
/// </summary>
public sealed class GeneralPoints : IFeature
{
    public const uint SciencePointsFloor = 20;

    // Rank.ini thresholds are small; anything larger is a sentinel or a bad read, and a kill on top could overflow the Int.
    private const uint MaxPlausibleThreshold = 1_000_000;

    public string Name => "General's points";


    public string Description => "Points to spend, promotion on next kill";

    public bool Enabled { get; set; }

    public bool NeedsArming => true;

    public void Apply(GameContext context)
    {
        var memory = context.Memory;
        uint player = context.LocalPlayer;

        uint pointsAddress = player + GameAddresses.PlayerSciencePurchasePoints;
        if (memory.ReadUInt32(pointsAddress) < SciencePointsFloor)
            memory.WriteUInt32(pointsAddress, SciencePointsFloor);

        uint skillAddress = player + GameAddresses.PlayerSkillPoints;
        uint? threshold = memory.ReadUInt32(player + GameAddresses.PlayerLevelUpThreshold);
        if (threshold is uint levelUp && levelUp <= MaxPlausibleThreshold && memory.ReadUInt32(skillAddress) < levelUp)
            memory.WriteUInt32(skillAddress, levelUp);
    }

    /// <summary>Points already granted stay; there is nothing to undo.</summary>
    public void Remove(IProcessMemory memory)
    {
    }
}
