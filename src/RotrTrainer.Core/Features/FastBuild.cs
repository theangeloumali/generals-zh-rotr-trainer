namespace RotrTrainer.Core.Features;

/// <summary>Units and structures build in a fraction of the time. Upgrades ignore the handicap and keep theirs.</summary>
public sealed class FastBuild() : HandicapScale(
    BuildTimeScale, GameAddresses.PlayerBuildTimeHandicapUnits, GameAddresses.PlayerBuildTimeHandicapBuildings)
{
    // One second of build time becomes one logic frame. Not zero: a zero-frame build divides by zero in the dozer path.
    public const float BuildTimeScale = 1f / 30f;

    public override string Name => "Fast build";

    public override string Description => "Units and structures, not upgrades";
}
