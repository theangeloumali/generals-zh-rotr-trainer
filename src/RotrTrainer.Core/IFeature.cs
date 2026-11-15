namespace RotrTrainer.Core;

/// <summary>What a feature sees on each tick; rebuilt every tick so no pointer is ever stale.</summary>
public sealed record GameContext(IProcessMemory Memory, GameAddresses Addresses, uint LocalPlayer, uint Frame);

public interface IFeature
{
    string Name { get; }

    /// <summary>One line for the window, saying what the player gets.</summary>
    string Description { get; }

    bool Enabled { get; set; }

    /// <summary>Why the feature could not be applied, or null. Value features cannot fail this way.</summary>
    string? Failure => null;

    /// <summary>False for fixes with no gameplay effect: they apply whenever a known build is attached.</summary>
    bool NeedsArming { get; }

    /// <summary>Called every tick while guards pass and the feature is enabled.</summary>
    void Apply(GameContext context);

    /// <summary>Undo anything left in the game. Must be safe to call when nothing was applied.</summary>
    void Remove(IProcessMemory memory);
}
