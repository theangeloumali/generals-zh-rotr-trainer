namespace RotrTrainer.Core.Features;

/// <summary>Keeps the local player's money at or above a floor; never lowers it.</summary>
public sealed class MoneyFloor : IFeature
{
    // Stays far below 0x7FFFFFFF because the HUD and AI read the balance as a signed Int.
    public const uint Floor = 1_000_000;

    public string Name => "Money floor";

    public bool Enabled { get; set; }

    public bool NeedsArming => true;

    public void Apply(GameContext context)
    {
        uint moneyAddress = context.LocalPlayer + GameAddresses.PlayerMoney;
        if (context.Memory.ReadUInt32(moneyAddress) < Floor)
            context.Memory.WriteUInt32(moneyAddress, Floor);
    }

    /// <summary>The money the player already has stays; there is nothing to undo.</summary>
    public void Remove(IProcessMemory memory)
    {
    }
}
