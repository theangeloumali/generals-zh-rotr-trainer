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

    private const uint MaxObjects = 1_000_000;
    private const int EntriesPerRead = 4096;
    private const int MaxModulesPerObject = 64;
    private const int TicksBetweenObjectWalks = 5;

    // An hour of game time; a "ready frame" further out than this is not a timer.
    private const uint MaxPlausibleWaitFrames = 30 * 60 * 60;

    private readonly Dictionary<uint, bool> _isSpecialPowerVtable = [];
    private int _ticksUntilObjectWalk;

    public string Name => "No cooldown";

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

        // IDs are never reused, so late in a long match the vector is large and mostly NULL: read it in blocks.
        uint entryCount = Math.Min((end - begin) / 4, MaxObjects);
        byte[] block = new byte[4 * EntriesPerRead];
        for (uint firstEntry = 0; firstEntry < entryCount; firstEntry += EntriesPerRead)
        {
            int entriesInBlock = (int)Math.Min(EntriesPerRead, entryCount - firstEntry);
            var entries = block.AsSpan(0, 4 * entriesInBlock);
            if (!memory.TryRead(begin + 4 * firstEntry, entries))
                return;

            for (int entry = 0; entry < entriesInBlock; entry++)
            {
                uint objectAddress = BitConverter.ToUInt32(entries.Slice(4 * entry, 4));
                if (objectAddress != 0 && OwnerOf(memory, objectAddress) == context.LocalPlayer)
                    MakeModuleTimersReady(context, objectAddress);
            }
        }
    }

    private static uint? OwnerOf(IProcessMemory memory, uint objectAddress) =>
        memory.ReadUInt32(objectAddress + GameAddresses.ObjectTeam) is { } team and not 0
        && memory.ReadUInt32(team + GameAddresses.TeamPrototype) is { } prototype and not 0
            ? memory.ReadUInt32(prototype + GameAddresses.TeamPrototypeOwner)
            : null;

    private void MakeModuleTimersReady(GameContext context, uint objectAddress)
    {
        var memory = context.Memory;
        uint behaviors = memory.ReadUInt32(objectAddress + GameAddresses.ObjectBehaviors) ?? 0;
        if (behaviors == 0)
            return;

        for (uint index = 0; index < MaxModulesPerObject; index++)
        {
            uint module = memory.ReadUInt32(behaviors + 4 * index) ?? 0;
            if (module == 0)
                return;
            if (IsSpecialPowerModule(memory, module))
                MakeReadyIfCharging(context, module + GameAddresses.SpecialPowerAvailableOnFrame);
        }
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
