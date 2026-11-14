namespace RotrTrainer.Core;

/// <summary>A game process the engine owns from attach until detach.</summary>
public sealed record AttachedGame(IProcessMemory Memory, string ExePath, int ProcessId, DateTime StartTimeUtc);

/// <summary>Returns the running game, or null with the reason it could not attach.</summary>
public delegate AttachedGame? GameAttacher(out string failure);

public sealed record FeatureStatus(string Name, bool Enabled, bool NeedsArming);

public sealed record TrainerStatus(
    bool IsAttached,
    string? BuildName,
    string? Refusal,
    bool IsArmed,
    string GuardReason,
    uint? Money,
    uint? SciencePurchasePoints,
    uint? RankLevel,
    IReadOnlyList<FeatureStatus> Features);

/// <summary>
/// The loop the UI drives: attach, re-resolve the local player, guard, apply. Starts and returns to disarmed on
/// every attach, guard failure, Player* change or frame rewind. Single-threaded: call every member from one thread.
/// </summary>
public sealed class TrainerEngine(
    IReadOnlyList<IFeature> features,
    GameAttacher attach,
    Func<int, bool?> ownsNetworkEndpoints) : IDisposable
{
    private readonly HashSet<IFeature> _applied = [];
    private AttachedGame? _game;
    private GameAddresses? _addresses;
    private string? _refusal;
    private GuardVerdict? _verdict;
    private bool _armed;
    private uint? _armedLocalPlayer;
    private uint _lastFrame;

    public TrainerStatus Status { get; private set; } = new(false, null, null, false, "no game attached", null, null, null, []);

    public void Tick()
    {
        if (_game is { Memory.IsAlive: false })
            Detach();
        if (_game is null && !TryAttach())
        {
            Publish();
            return;
        }

        if (_game is { } game && _addresses is { } addresses)
            RunGuardedAgainstDeath(game, () => ApplyFeatures(game, addresses));
        Publish();
    }

    /// <summary>Arms only while every guard passes right now; the next Player* change or rewind disarms again.</summary>
    public bool Arm()
    {
        if (_game is not { } game || _addresses is not { } addresses)
            return false;

        _verdict = Evaluate(game, addresses);
        if (_verdict.MayArm)
        {
            var state = new GameState(game.Memory, addresses);
            _armedLocalPlayer = state.LocalPlayer();
            _lastFrame = state.Frame() ?? 0;
            _armed = true;
        }
        Publish();
        return _armed;
    }

    public void Disarm()
    {
        _armed = false;
        RemoveApplied(feature => feature.NeedsArming);
        Publish();
    }

    public void Dispose()
    {
        Detach();
        Publish();
    }

    private bool TryAttach()
    {
        _game = attach(out string failure);
        if (_game is null)
        {
            _refusal = failure;
            return false;
        }

        var catalog = BuildCatalog.Resolve(_game.ExePath);
        _addresses = catalog.Addresses;
        _refusal = catalog.Refusal;
        return true;
    }

    private void ApplyFeatures(AttachedGame game, GameAddresses addresses)
    {
        var state = new GameState(game.Memory, addresses);
        uint? localPlayer = state.LocalPlayer();
        uint? frame = state.Frame();
        _verdict = Evaluate(game, addresses);

        if (_armed && (!_verdict.MayArm || localPlayer != _armedLocalPlayer || frame is null || frame < _lastFrame))
            Disarm();
        if (_armed)
            _lastFrame = frame ?? _lastFrame;

        var context = new GameContext(game.Memory, addresses, localPlayer ?? 0, frame ?? 0);
        foreach (var feature in features)
        {
            if (feature.Enabled && (_armed || !feature.NeedsArming))
            {
                feature.Apply(context);
                _applied.Add(feature);
            }
            else if (_applied.Remove(feature))
            {
                feature.Remove(game.Memory);
            }
        }
    }

    private GuardVerdict Evaluate(AttachedGame game, GameAddresses addresses) =>
        Guards.Evaluate(game.Memory, addresses, () => ownsNetworkEndpoints(game.ProcessId));

    private void RemoveApplied(Func<IFeature, bool> shouldRemove)
    {
        var removing = _applied.Where(shouldRemove).ToList();
        _applied.ExceptWith(removing);
        if (_game is not { Memory.IsAlive: true } game)
            return;
        RunGuardedAgainstDeath(game, () => removing.ForEach(feature => feature.Remove(game.Memory)));
    }

    // A suspend or write that fails because the game just exited is a state change, not an error.
    private void RunGuardedAgainstDeath(AttachedGame game, Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException) when (!game.Memory.IsAlive)
        {
            Detach();
        }
    }

    private void Detach()
    {
        _armed = false;
        RemoveApplied(_ => true);
        if (_game?.Memory is IDisposable disposable)
            disposable.Dispose();
        _game = null;
        _addresses = null;
        _verdict = null;
        _refusal = null;
    }

    private void Publish()
    {
        var memory = _game?.Memory;
        uint? localPlayer = memory is not null && _addresses is { } addresses
            ? new GameState(memory, addresses).LocalPlayer()
            : null;
        uint? ReadPlayer(uint offset) => memory is not null && localPlayer is uint player and not 0
            ? memory.ReadUInt32(player + offset)
            : null;

        Status = new TrainerStatus(
            IsAttached: _game is not null,
            BuildName: _addresses?.BuildName,
            Refusal: _refusal,
            IsArmed: _armed,
            GuardReason: _verdict?.Reason ?? _refusal ?? "no game attached",
            Money: ReadPlayer(GameAddresses.PlayerMoney),
            SciencePurchasePoints: ReadPlayer(GameAddresses.PlayerSciencePurchasePoints),
            RankLevel: ReadPlayer(GameAddresses.PlayerRankLevel),
            Features: features.Select(feature => new FeatureStatus(feature.Name, feature.Enabled, feature.NeedsArming)).ToList());
    }
}
