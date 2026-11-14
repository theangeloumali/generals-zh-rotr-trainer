namespace RotrTrainer.Core;

/// <summary>
/// Reads live game globals. Null means the read failed or the value does not exist yet;
/// 0 means the game's own pointer is null (main menu, between matches).
/// </summary>
public sealed class GameState(IProcessMemory memory, GameAddresses addresses)
{
    public uint? GameLogic() => memory.ReadUInt32(addresses.GameLogicPointer);

    public uint? LocalPlayer()
    {
        uint? playerList = memory.ReadUInt32(addresses.PlayerListPointer);
        if (playerList is not uint list || list == 0)
            return playerList;
        return memory.ReadUInt32(list + GameAddresses.PlayerListLocalPlayer);
    }

    public uint? Frame() => ReadFromGameLogic(GameAddresses.GameLogicFrame);

    public GameMode? Mode()
    {
        uint? raw = ReadFromGameLogic(GameAddresses.GameLogicGameMode);
        if (raw is not uint value || value > int.MaxValue || !Enum.IsDefined(typeof(GameMode), (int)value))
            return null;
        return (GameMode)value;
    }

    private uint? ReadFromGameLogic(uint fieldOffset)
    {
        uint? gameLogic = GameLogic();
        return gameLogic is uint logic && logic != 0 ? memory.ReadUInt32(logic + fieldOffset) : null;
    }
}
