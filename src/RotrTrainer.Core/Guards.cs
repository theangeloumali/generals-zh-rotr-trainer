namespace RotrTrainer.Core;

public sealed record GuardVerdict(bool MayArm, string Reason)
{
    internal static readonly GuardVerdict Allowed = new(true, "local single player or skirmish game");

    internal static GuardVerdict Refused(string reason) => new(false, reason);
}

public static class Guards
{
    /// <summary>
    /// First failing check wins; any unreadable value refuses. <paramref name="ownsNetworkEndpoints"/>
    /// returns null when the endpoint query itself failed.
    /// </summary>
    public static GuardVerdict Evaluate(IProcessMemory memory, GameAddresses addresses, Func<bool?> ownsNetworkEndpoints)
    {
        if (!memory.IsAlive)
            return GuardVerdict.Refused("game process is not running");

        var state = new GameState(memory, addresses);
        switch (state.GameLogic())
        {
            case null:
                return GuardVerdict.Refused("could not read TheGameLogic");
            case 0:
                return GuardVerdict.Refused("no game in progress (TheGameLogic is null)");
        }

        GameMode? mode = state.Mode();
        if (mode is null)
            return GuardVerdict.Refused("could not read the game mode");
        if (mode is not (GameMode.SinglePlayer or GameMode.Skirmish))
            return GuardVerdict.Refused($"game mode {mode} is not single player or skirmish");

        switch (state.LocalPlayer())
        {
            case null:
                return GuardVerdict.Refused("could not read the local player");
            case 0:
                return GuardVerdict.Refused("no local player yet");
            case uint localPlayer when LocalPlayerRefusal(memory, localPlayer) is string refusal:
                return GuardVerdict.Refused(refusal);
        }

        return ownsNetworkEndpoints() switch
        {
            null => GuardVerdict.Refused("network endpoint query failed"),
            true => GuardVerdict.Refused("game process owns network endpoints"),
            false => GuardVerdict.Allowed,
        };
    }

    private const uint HumanPlayerType = 0;

    // docs/anchors.md "Player offsets": m_playerType is an Int, m_observer and m_isPlayerDead are Bool bytes.
    private static string? LocalPlayerRefusal(IProcessMemory memory, uint localPlayer)
    {
        Span<byte> observer = stackalloc byte[1];
        Span<byte> dead = stackalloc byte[1];
        uint? playerType = memory.ReadUInt32(localPlayer + GameAddresses.PlayerType);
        if (playerType is null
            || !memory.TryRead(localPlayer + GameAddresses.PlayerIsObserver, observer)
            || !memory.TryRead(localPlayer + GameAddresses.PlayerIsDead, dead))
            return "could not read the local player's flags";
        if (observer[0] != 0)
            return "local player is an observer";
        if (dead[0] != 0)
            return "local player is defeated";
        return playerType == HumanPlayerType ? null : "local player is not human";
    }
}
