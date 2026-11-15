namespace RotrTrainer.Core.Features;

/// <summary>
/// Makes the local player's special powers ready now: the shared general-power timers on the player record,
/// and the per-building and per-unit timers (superweapons, abilities). Plain value changes only.
/// </summary>
public sealed class SharedPowerCooldown : IFeature
{
    public const uint NodeReadyFrame = 0x0C;

    // A general has a handful of shared powers; anything longer is a list we misread.
    public const int MaxNodes = 64;

    // getSpecialPower() of every SpecialPowerModule: lea eax,[ecx-10h] · test · je · lea eax,[ecx+4] · ret.
    // Other modules return NULL there, so these bytes identify a special-power module without a class list.
    public static readonly byte[] SpecialPowerAccessorCode =
        [0x8D, 0x41, 0xF0, 0x85, 0xC0, 0x74, 0x04, 0x8D, 0x41, 0x04, 0xC3];

    // The table doubles as IDs climb; eight million entries is far past any real match.
    private const uint MaxObjects = 8_388_608;
    private const int MaxListNodesPerWalk = 4_000;

    // One walk covers this many table entries and the next resumes after them, so a huge or corrupt table
    // cannot stall the trainer.
    private const uint MaxEntriesPerWalk = 65_536;
    private const int MaxCachedVtables = 1024;
    private const int EntriesPerRead = 4096;
    private const int MaxModulesPerObject = 64;
    private const int TicksBetweenObjectWalks = 5;

    // An hour of game time; a "ready frame" further out than this is not a timer.
    private const uint MaxPlausibleWaitFrames = 30 * 60 * 60;

    private readonly Dictionary<uint, bool> _isSpecialPowerVtable = [];
    private int _ticksUntilObjectWalk;
    private uint _nextEntry;
    private uint _listCursor;
    private int _ownedThisPass;
    private int _withPowerThisPass;
    private (int Owned, int WithPower)? _lastScan;

    public string Name => "No cooldown";


    // After a full pass over the object table the row says what was found, so "not working" can be told
    // apart from "nothing of yours has a power".
    public string Description => _lastScan is var (owned, withPower)
        ? $"{owned} of your units and buildings seen, {withPower} with a power"
        : "General powers and superweapons";

    public bool Enabled { get; set; }

    public bool NeedsArming => true;

    public void Apply(GameContext context)
    {
        MakeSharedTimersReady(context);
        if (_ticksUntilObjectWalk-- <= 0)
        {
            _ticksUntilObjectWalk = TicksBetweenObjectWalks - 1;
            MakeObjectTimersReady(context);
        }
    }

    /// <summary>A power that is ready stays ready; only the per-process caches are dropped.</summary>
    public void Remove(IProcessMemory memory)
    {
        _isSpecialPowerVtable.Clear();
        _ticksUntilObjectWalk = 0;
        _nextEntry = 0;
        _listCursor = 0;
        _ownedThisPass = 0;
        _withPowerThisPass = 0;
        _lastScan = null;
    }

    private static void MakeSharedTimersReady(GameContext context)
    {
        if (context.Memory.ReadUInt32(context.LocalPlayer + GameAddresses.PlayerSharedPowerTimerList) is not { } sentinel)
            return;

        uint? node = context.Memory.ReadUInt32(sentinel);
        for (int visited = 0; node is { } current && current != sentinel && visited < MaxNodes; visited++)
        {
            MakeReadyIfCharging(context, current + NodeReadyFrame);
            node = context.Memory.ReadUInt32(current);
        }
    }

    private void MakeObjectTimersReady(GameContext context)
    {
        var memory = context.Memory;
        if (memory.ReadUInt32(context.Addresses.GameLogicPointer) is not { } gameLogic
            || memory.ReadUInt32(gameLogic + GameAddresses.GameLogicObjectVectorBegin) is not { } begin
            || memory.ReadUInt32(gameLogic + GameAddresses.GameLogicObjectVectorEnd) is not { } end
            || end < begin)
        {
            return;
        }

        uint tableEntries = (end - begin) / 4;
        if (!WalkLiveObjects(context, gameLogic, begin, tableEntries))
            ScanLookupTable(context, begin, tableEntries);
    }

    /// <summary>
    /// Follows the game's list of live objects, a few thousand nodes however long the match has run. Each node
    /// must be the object the lookup table holds for its ID; false means the list could not be trusted.
    /// </summary>
    private bool WalkLiveObjects(GameContext context, uint gameLogic, uint tableBegin, uint tableEntries)
    {
        var memory = context.Memory;
        uint current = _listCursor != 0 ? _listCursor
            : memory.ReadUInt32(gameLogic + GameAddresses.GameLogicFirstObject) ?? 0;
        if (current == 0)
            return false;
        if (_listCursor == 0)
        {
            _ownedThisPass = 0;
            _withPowerThisPass = 0;
        }

        for (int visited = 0; visited < MaxListNodesPerWalk; visited++)
        {
            if (memory.ReadUInt32(current + GameAddresses.ObjectId) is not { } id
                || id >= tableEntries
                || memory.ReadUInt32(tableBegin + 4 * id) != current)
            {
                // Not a live object: wrong offsets, or the node died since the last walk. Start over next time.
                bool failedAtHead = _listCursor == 0 && visited == 0;
                _listCursor = 0;
                return !failedAtHead;
            }

            VisitObject(context, current);
            current = memory.ReadUInt32(current + GameAddresses.ObjectNext) ?? 0;
            if (current == 0)
            {
                _listCursor = 0;
                _lastScan = (_ownedThisPass, _withPowerThisPass);
                return true;
            }
        }

        _listCursor = current;
        return true;
    }

    private void VisitObject(GameContext context, uint objectAddress)
    {
        if (OwnerOf(context.Memory, objectAddress) != context.LocalPlayer)
            return;
        _ownedThisPass++;
        if (MakeModuleTimersReady(context, objectAddress))
            _withPowerThisPass++;
    }

    /// <summary>Fallback when the live list cannot be trusted: read the ID lookup table itself, in blocks.</summary>
    private void ScanLookupTable(GameContext context, uint begin, uint tableEntries)
    {
        var memory = context.Memory;
        // IDs are never reused, so late in a long match the table is large and mostly NULL.
        uint entryCount = Math.Min(tableEntries, MaxObjects);
        byte[] block = new byte[4 * EntriesPerRead];
        if (_nextEntry >= entryCount)
            _nextEntry = 0;
        if (_nextEntry == 0)
        {
            _ownedThisPass = 0;
            _withPowerThisPass = 0;
        }
        uint walkEnd = Math.Min(entryCount, _nextEntry + MaxEntriesPerWalk);
        for (uint firstEntry = _nextEntry; firstEntry < walkEnd; firstEntry += EntriesPerRead)
        {
            int entriesInBlock = (int)Math.Min(EntriesPerRead, walkEnd - firstEntry);
            var entries = block.AsSpan(0, 4 * entriesInBlock);
            if (!memory.TryRead(begin + 4 * firstEntry, entries))
            {
                _nextEntry = 0;
                return;
            }

            for (int entry = 0; entry < entriesInBlock; entry++)
            {
                uint objectAddress = BitConverter.ToUInt32(entries.Slice(4 * entry, 4));
                if (objectAddress != 0)
                    VisitObject(context, objectAddress);
            }
        }
        _nextEntry = walkEnd;
        if (walkEnd >= entryCount)
            _lastScan = (_ownedThisPass, _withPowerThisPass);
    }

    private static uint? OwnerOf(IProcessMemory memory, uint objectAddress) =>
        memory.ReadUInt32(objectAddress + GameAddresses.ObjectTeam) is { } team and not 0
        && memory.ReadUInt32(team + GameAddresses.TeamPrototype) is { } prototype and not 0
            ? memory.ReadUInt32(prototype + GameAddresses.TeamPrototypeOwner)
            : null;

    /// <summary>True when the object carries at least one special-power module.</summary>
    private bool MakeModuleTimersReady(GameContext context, uint objectAddress)
    {
        var memory = context.Memory;
        uint behaviors = memory.ReadUInt32(objectAddress + GameAddresses.ObjectBehaviors) ?? 0;
        if (behaviors == 0)
            return false;

        bool hasPower = false;

        for (uint index = 0; index < MaxModulesPerObject; index++)
        {
            uint module = memory.ReadUInt32(behaviors + 4 * index) ?? 0;
            if (module == 0)
                break;
            if (IsSpecialPowerModule(memory, module))
            {
                hasPower = true;
                MakeReadyIfCharging(context, module + GameAddresses.SpecialPowerAvailableOnFrame);
            }
        }
        return hasPower;
    }

    private bool IsSpecialPowerModule(IProcessMemory memory, uint module)
    {
        if (memory.ReadUInt32(module + GameAddresses.ModuleInterfaceVtable) is not { } vtable)
            return false;
        if (_isSpecialPowerVtable.TryGetValue(vtable, out bool known))
            return known;

        Span<byte> code = stackalloc byte[SpecialPowerAccessorCode.Length];
        bool isSpecialPower = memory.ReadUInt32(vtable + GameAddresses.InterfaceGetSpecialPowerSlot) is { } accessor
            && memory.TryRead(accessor, code)
            && code.SequenceEqual(SpecialPowerAccessorCode);
        if (_isSpecialPowerVtable.Count >= MaxCachedVtables)
            _isSpecialPowerVtable.Clear();
        _isSpecialPowerVtable[vtable] = isSpecialPower;
        return isSpecialPower;
    }

    private static void MakeReadyIfCharging(GameContext context, uint readyFrameAddress)
    {
        if (context.Memory.ReadUInt32(readyFrameAddress) is { } readyFrame
            && readyFrame > context.Frame
            && readyFrame - context.Frame <= MaxPlausibleWaitFrames)
        {
            context.Memory.WriteUInt32(readyFrameAddress, context.Frame);
        }
    }
}
