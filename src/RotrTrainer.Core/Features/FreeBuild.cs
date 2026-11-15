namespace RotrTrainer.Core.Features;

/// <summary>Units and structures cost nothing: the engine truncates the scaled cost to zero.</summary>
public sealed class FreeBuild() : HandicapScale(
    BuildCostScale, GameAddresses.PlayerBuildCostHandicapUnits, GameAddresses.PlayerBuildCostHandicapBuildings)
{
    // Not exactly zero: freed memory is often zeroed, and Remove tells "still ours" by this bit pattern.
    public const float BuildCostScale = 0.00001f;

    public override string Name => "Free build";

    public override string Description => "Units and structures cost nothing";
}
