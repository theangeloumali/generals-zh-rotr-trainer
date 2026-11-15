namespace RotrTrainer.Core;

/// <summary>A game process the engine owns from attach until detach.</summary>
public sealed record AttachedGame(IProcessMemory Memory, string ExePath, int ProcessId, DateTime StartTimeUtc);

/// <summary>Returns the running game, or null with the reason it could not attach.</summary>
public delegate AttachedGame? GameAttacher(out string failure);

public sealed record FeatureStatus(string Name, string Description, bool Enabled, bool NeedsArming, string? Failure = null);

public sealed record TrainerStatus(
    bool IsAttached,
    string? BuildName,
    string? Refusal,
    bool IsArmed,
    bool MayArm,
    bool GenToolPresent,
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
    private const string GenToolProxyDll = "d3d8.dll";

    private readonly HashSet<IFeature> _applied = [];
    private AttachedGame? _game;
    private GameAddresses? _addresses;
    private string? _refusal;
    private GuardVerdict? _verdict;
    private bool _armed;
    private uint? _armedLocalPlayer;
    private uint _lastFrame;
    private bool _genToolPresent;

    public TrainerStatus Status { get; private set; } = new(false, null, null, false, false, false, "no game attached", null, null, null, []);

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
        if (!_verdict.MayArm)
        {
            Disarm();
            return false;
        }

        var state = new GameState(game.Memory, addresses);
        _armedLocalPlayer = state.LocalPlayer();
        _lastFrame = state.Frame() ?? 0;
        _armed = true;
        Publish();
        return true;
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

        // GenTool ships as a d3d8.dll proxy beside the game; its presence on disk needs no extra process access.
        _genToolPresent = File.Exists(Path.Combine(Path.GetDirectoryName(_game.ExePath) ?? "", GenToolProxyDll));
        var catalog = BuildCatalog.Resolve(_game.ExePath);
        _addresses = catalog.Addresses;
        _refusal = catalog.Refusal;
        if (_addresses is { } addresses && !RunningImageMatchesFile(_game.Memory, addresses))
        {
            // The file can be replaced on disk while an older image keeps running; its addresses would be wrong.
            _addresses = null;
            _refusal = "the running game does not match its file on disk; restart the game";
        }
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
            // A fix that needs no arming still stays out of LAN, online and replay games.
            if (feature.Enabled && (_armed || (!feature.NeedsArming && _verdict.MayArm)))
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

    private static bool RunningImageMatchesFile(IProcessMemory memory, GameAddresses addresses)
    {
        byte[] live = new byte[addresses.ImageCheckBytes.Length];
        return memory.TryRead(addresses.ImageCheckAddress, live) && live.AsSpan().SequenceEqual(addresses.ImageCheckBytes);
    }

    private GuardVerdict Evaluate(AttachedGame game, GameAddresses addresses) =>
        Guards.Evaluate(game.Memory, addresses, () => ownsNetworkEndpoints(game.ProcessId));

    private void RemoveApplied(Func<IFeature, bool> shouldRemove)
    {
        var removing = _applied.Where(shouldRemove).ToList();
        _applied.ExceptWith(removing);
        if (_game is not { } game)
            return;

        // A dead process still gets the call: nothing can be undone there, but each feature must drop the
        // addresses and caches it holds, or they would be trusted in the next game process.
        foreach (var feature in removing)
        {
            try
            {
                feature.Remove(game.Memory);
            }
            catch (InvalidOperationException) when (!game.Memory.IsAlive)
            {
            }
        }
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
        _genToolPresent = false;
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
            MayArm: _verdict?.MayArm ?? false,
            GenToolPresent: _genToolPresent,
            GuardReason: _verdict?.Reason ?? _refusal ?? "no game attached",
            Money: ReadPlayer(GameAddresses.PlayerMoney),
            SciencePurchasePoints: ReadPlayer(GameAddresses.PlayerSciencePurchasePoints),
            RankLevel: ReadPlayer(GameAddresses.PlayerRankLevel),
            Features: features.Select(feature => new FeatureStatus(feature.Name, feature.Description, feature.Enabled, feature.NeedsArming, feature.Failure)).ToList());
    }
}
