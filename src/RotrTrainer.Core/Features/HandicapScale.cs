namespace RotrTrainer.Core.Features;

/// <summary>
/// Overwrites a pair of the local player's handicap multipliers, plain values the engine applies to every
/// unit and structure. No game code is changed.
/// </summary>
public abstract class HandicapScale(float scale, uint unitsOffset, uint buildingsOffset) : IFeature
{
    private readonly uint _scaleBits = BitConverter.SingleToUInt32Bits(scale);
    private readonly Dictionary<uint, uint> _originalBitsByAddress = [];

    public abstract string Name { get; }

    public bool Enabled { get; set; }

    public bool NeedsArming => true;

    public void Apply(GameContext context)
    {
        foreach (uint offset in (uint[])[unitsOffset, buildingsOffset])
        {
            uint address = context.LocalPlayer + offset;
            uint? currentBits = context.Memory.ReadUInt32(address);
            if (currentBits is null || currentBits == _scaleBits)
                continue;
            if (context.Memory.WriteUInt32(address, _scaleBits))
                _originalBitsByAddress[address] = currentBits.Value;
        }
    }

    public void Remove(IProcessMemory memory)
    {
        // A match that ended frees the player record; restore only where our own value is still in place.
        bool stillOurs = _originalBitsByAddress.Count > 0
            && _originalBitsByAddress.Keys.All(address => memory.ReadUInt32(address) == _scaleBits);
        if (stillOurs)
        {
            foreach (var (address, originalBits) in _originalBitsByAddress)
                memory.WriteUInt32(address, originalBits);
        }
        _originalBitsByAddress.Clear();
    }
}
