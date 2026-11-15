namespace RotrTrainer.Core.Features;

/// <summary>
/// Adds a large bonus to the local player's power production so the base never runs short, and expires
/// the sabotage timer. A plain value change; a brown-out already in effect lifts at the next power change.
/// </summary>
public sealed class PowerBonus : IFeature
{
    public const uint Bonus = 1_000_000;

    // Player::update lifts the brown-out only when the timer is non-zero and already past; zero would skip that.
    public const uint ExpiredSabotageFrame = 1;

    private uint? _productionAddress;

    public string Name => "Unlimited power";


    public string Description => "Base never runs short";

    public bool Enabled { get; set; }

    public bool NeedsArming => true;

    public void Apply(GameContext context)
    {
        uint productionAddress = context.LocalPlayer + GameAddresses.PlayerEnergyProduction;
        // Below the bonus means it was never added, or the game rebuilt the record (restart), so add it again.
        if (context.Memory.ReadUInt32(productionAddress) is { } production && production < Bonus
            && context.Memory.WriteUInt32(productionAddress, production + Bonus))
        {
            _productionAddress = productionAddress;
        }

        uint sabotageAddress = context.LocalPlayer + GameAddresses.PlayerEnergySabotageFrame;
        if (context.Memory.ReadUInt32(sabotageAddress) is > ExpiredSabotageFrame)
            context.Memory.WriteUInt32(sabotageAddress, ExpiredSabotageFrame);
    }

    public void Remove(IProcessMemory memory)
    {
        // Subtract only while the bonus is visibly still there, so production can never go negative.
        if (_productionAddress is { } address && memory.ReadUInt32(address) is { } production && production >= Bonus)
            memory.WriteUInt32(address, production - Bonus);
        _productionAddress = null;
    }
}
