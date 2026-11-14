# Anchors: static proof for the trainer's guards and code patches

Static analysis notes. Static only: nothing was launched or attached. Inputs: `modded.exe` (sha256 `4ea7e86b…c887`) and `Game.dat` (sha256 `f37a4929…495b`), both ZH 1.04 code at image base 0x400000 with no `.reloc` section, so absolute operands are fixed per build. Source: EA GPL `GeneralsMD/Code/GameEngine` (paths below are relative to it). Tools: `objdump -M intel` on address ranges, python3 regex signature scans. Scratch scripts: `scratchpad/anchors/{h,fn,sites}.py`.

Labels: **PROVEN** means a store or accessor at the cited VA that only the cited source line explains. **UNPROVEN** items are listed in their own section with the runtime read that settles them.

Every signature below was scanned over `.text` of both files and matches **exactly once in each**. `??` is a wildcard byte. "+N" after a signature is the offset from the match to the cited VA.

Per-build constants used by the caves (resolve them from the signatures, never from a fixed delta; per-function deltas range from −0x2E0 to +0x150):

| Constant | modded.exe | Game.dat | Source of the Game.dat value |
| --- | --- | --- | --- |
| `&ThePlayerList` | 0xA2B600 | 0xA2B688 | `01-binary-teardown.md` |
| `&TheGameLogic` | 0xA2BB64 | 0xA2BBEC | operand of the game-mode store signature at 0x495583 (`A1 EC BB A2 00`) |
| `Object::getControllingPlayer` | 0x547C30 | 0x547A00 | `call` at startPowerRecharge+0x16 |
| `Energy::hasSufficientPower` | 0x577230 | 0x576F50 | `call` at the Player::update inline site −5 |
| 30.0f build-time constant | 0x93BEDC | 0x93BEE4 | `fmul` operand in UpgradeTemplate::calcTimeToBuild (both read 30.0) |

Local-player resolution in a cave: `mov edx,[&ThePlayerList]; test edx,edx; jz orig; mov edx,[edx+0xC]` (m_local, PROVEN in `02`).

## Game mode

**`TheGameLogic + 0x94` is `m_gameMode`: PROVEN** by two independent stores.

| Evidence | modded.exe | Game.dat | Bytes / disassembly | Source |
| --- | --- | --- | --- | --- |
| Constructor store | 0x4A1E7A | 0x4A200A | `mov dword [esi+0x94],6` then `mov dword [esi+0x98],0x3E8` | `GameLogic.cpp:252-253` `m_gameMode = GAME_NONE; m_rankLevelLimit = 1000;` |
| `prepareNewGame` store | 0x4954F3 | 0x495583 | `mov eax,[0xA2BB64]; mov edx,[esp+0x2C]; mov [eax+0x94],edx` | `GameLogicDispatch.cpp:329` `TheGameLogic->setGameMode( gameMode );` |

- The constructor pair is decisive. `m_gameMode` and `m_rankLevelLimit` are adjacent `Int` members (`GameLogic.h:352-353`), and the only `+0x94 ← 6` store in `.text` is immediately followed by `+0x98 ← 1000`. GAME_NONE is enum value 6 (`GameLogic.h:74-81`: SINGLE_PLAYER 0, LAN 1, SKIRMISH 2, REPLAY 3, SHELL 4, INTERNET 5, NONE 6).
- The `prepareNewGame` site stores a stack value through `*TheGameLogic`, which corroborates the setter.
- Signatures:
  - ctor: `C7 86 94 00 00 00 06 00 00 00 C7 86 98 00 00 00 E8 03 00 00`
  - setter: `A1 ?? ?? ?? ?? 8B 54 24 2C 89 90 94 00 00 00` (the wildcard is `&TheGameLogic` for that build).
- Guard rule (unchanged from `04` #1): armed only when `m_gameMode ∈ {0, 2}`. 6 (between games) and 4 (shell) are disarmed.

## Patch sites

Common rules for every site:

- Write `E9 rel32` at the span start, then fill the rest of the span with `90`.
- The cave runs the guard chain from `04` #1 (TheGameLogic non-NULL, mode ∈ {0,2}, armed byte, expiry frame) plus the local-player compare. If any check fails, it runs the stolen bytes verbatim and jumps back to the return address.
- Copy stolen bytes from the target's own image, never from this table. Two of them contain an absolute operand that differs per build.
- "Free registers" are the ones the cave may clobber without saving them.

### Crash fix: AIUpdateInterface::privateMoveAwayFromUnit

| Field | Value |
| --- | --- |
| Function | modded 0x5D4BE0, Game.dat 0x5D49D0. Signature: `51 53 55 56 8B F1 8A 86 F9 01 00 00 84 C0 57 0F 85 ?? ?? ?? ?? 8B 4E 0C E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 8B 06 8B CE FF 90 34 01 00 00 84 C0 0F 84 ?? ?? ?? ?? 8B 46 2C 8B 40 4C 85 C0 8B 54 24 18 8B 4A 64 74 ??` |
| Site signature | `85 C0 8B 54 24 18 8B 4A 64 74 ?? 83 78 08 17` +2 → modded 0x5D4C1F, Game.dat 0x5D4A0F |
| Span | 7 bytes, 2 whole instructions: `8B 54 24 18` mov edx,[esp+0x18] · `8B 4A 64` mov ecx,[edx+0x64] (the faulting load) |
| Position-independent | Yes |
| Return to | +7: modded 0x5D4C26, Game.dat 0x5D4A16 (`74 65` je, which consumes ZF) |
| Early-exit target | modded 0x5D4E17, Game.dat 0x5D4C07: `pop edi; pop esi; pop ebp; pop ebx; pop ecx; ret 8` |
| Convention / state | thiscall `(Object *unit, CommandSourceType)`, `ret 8`. Prologue pushes ecx, ebx, ebp, esi, edi (0x14 bytes). At the site `this` = esi and `unit` = `[esp+0x18]`. Calls before the site are argument-less thiscalls, so the stack depth at the site equals the depth at 0x5D4E17. |
| Must preserve | eax (temporary-state pointer read by the `cmp [eax+8],0x17` at +9), esi, edi, ebx, ebp. The original `test eax,eax` at −2 sets the ZF the `je` at +7 reads. |
| Local-player compare | None. The unit pointer is not scoped to a player. Keep the armed/mode guard (`04` #1) and use ecx as scratch, since the span reloads it. |
| Callers | 0 direct calls; reached through 15 vtable slots. Patching the body covers all of them. |

Cave:

```
mov  edx,[esp+0x18]      ; unit
test edx,edx
jz   EXIT                ; 0x5D4E17 / 0x5D4C07 (rel32 from the cave)
mov  ecx,[edx+0x64]      ; unit->getID()
test eax,eax             ; re-create the flags the je at the return address reads
jmp  RET                 ; 0x5D4C26 / 0x5D4A16
```

A cave-free in-place rewrite does not fit. The 11 bytes from 0x5D4C1D to 0x5D4C27 would need 15. The two `ret 8` epilogues within short-jump reach (0x5D4C4D, 0x5D4C7C) both write `m_ignoreCollisionTime` (`[esi+0x178]`) before returning. The NULL test returns before any state change, which matches the community fork's early return. Source: `Object/Update/AIUpdate.cpp:3217` (`unit->getID()`).

### Instant build: ThingTemplate::calcTimeToBuild

| Field | Value |
| --- | --- |
| Function | modded 0x58C8A0, Game.dat 0x58C650. Signature: `83 EC 08 53 56 57 8B 7C 24 18 8B F1 8B 86 ?? ?? 00 00 56 6A 01` |
| Span | 5 bytes, 3 whole instructions: `83 EC 08` sub esp,8 · `53` push ebx · `56` push esi |
| Position-independent | Yes |
| Return to | +5: modded 0x58C8A5, Game.dat 0x58C655 (`push edi`) |
| Convention | thiscall `Int (const Player *player) const`, `ret 4`. At entry ecx = ThingTemplate and `[esp+4]` = player. The function has two `ret 4` exits (0x58CA74, 0x58CA7F); none branches into the span. |
| Free registers | eax, edx. Preserve ecx. |
| Local-player compare | `[esp+4] == local` → `mov eax,1; ret 4` |
| Callers | 2 in both builds: ProductionUpdate 0x5A0BCA and DozerAIUpdate 0x648BD4 (`02`) |

### Instant build: UpgradeTemplate::calcTimeToBuild

| Field | Value |
| --- | --- |
| Function | modded 0x4AEAF0, Game.dat 0x4AEC30. Signature: `D9 41 14 D8 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? C2 04 00 90` (the trailing `90` padding is included to lengthen the signature; it still matches once in each build) |
| Span | **9 bytes**, 2 whole instructions: `D9 41 14` fld dword [ecx+0x14] · `D8 0D <abs32>` fmul dword [30.0f]. A 5-byte span would cut the `fmul` (`04` #4). |
| Stolen bytes | modded `D9 41 14 D8 0D DC BE 93 00` · Game.dat `D9 41 14 D8 0D E4 BE 93 00` |
| Position-independent | Yes. The `fmul` operand is absolute, the image is not relocatable, and the bytes are copied verbatim per build. |
| Return to | +9: modded 0x4AEAF9, Game.dat 0x4AEC39 (`call _ftol`, then `ret 4`) |
| Convention | thiscall `Int (Player *player) const`, `ret 4`. At entry ecx = UpgradeTemplate and `[esp+4]` = player (still passed, although retail ignores it). |
| Free registers | eax, edx. Preserve ecx. The local branch must leave the x87 stack untouched. |
| Local-player compare | `[esp+4] == local` → `mov eax,1; ret 4` |
| Callers | 1 in both builds: ProductionUpdate 0x5A0BD1 |

The whole function (17 bytes) is not a usable span, because it contains the rel32 `call _ftol`.

### No cooldown: SpecialPowerModule::startPowerRecharge

| Field | Value |
| --- | --- |
| Function | modded 0x686E10, Game.dat 0x686F60. Signature: `53 56 8B F1 57 8B 7E F4 8B 47 08 85 C0 74 ?? 8B 4E F8 85 C9 74 ?? E8` |
| Span | 5 bytes, 4 whole instructions: `53` push ebx · `56` push esi · `8B F1` mov esi,ecx · `57` push edi |
| Position-independent | Yes |
| Return to | +5: modded 0x686E15, Game.dat 0x686F65 |
| Convention | thiscall `void ()`, plain `ret`. ecx = the SpecialPowerModuleInterface sub-object (module+0x14): `[ecx−0xC]` = module data, `[ecx−0x8]` = Object*. |
| Player | `getControllingPlayer` (modded 0x547C30, Game.dat 0x547A00), thiscall with ecx = Object*, returns Player* in eax. |
| Free registers | eax, edx. Save ecx around the call (`push ecx … pop ecx`); getControllingPlayer clobbers eax, ecx and edx. |
| Local-player compare | `obj = [ecx−8]`; if obj is NULL, go to the original path. Otherwise `eax = getControllingPlayer(obj)`; if `eax == local` → `ret`. |
| Callers | 0 direct calls; reached through 10 vtable slots. Covers shared/synced timers (via `resetOrStartSpecialPowerReadyFrame`, 0x456AB0 / 0x456870) and per-module `m_availableOnFrame` (sub-object+4). |

### No cooldown: inlined startPowerRecharge in the SpecialPowerModule constructor

| Field | Value |
| --- | --- |
| Function | Constructor `SpecialPowerModule::SpecialPowerModule`, modded 0x686720, Game.dat 0x686870 (SEH prologue; sets 4 vtables; zeroes `[esi+0x18..0x24]` at 0x686783 = `m_availableOnFrame, m_pausedCount, m_pausedOnFrame, m_pausedPercent`, `SpecialPowerModule.cpp:104-107`) |
| Site signature | `74 0D 8B 43 08 50 8B CD E8 ?? ?? ?? ?? EB 1F 8B 0D ?? ?? ?? ?? 8B 59 3C 8B 17 8B CF FF 52 14 8B C8 E8 ?? ?? ?? ?? 8B 40 18 03 C3 89 47 04` +0xF → modded 0x6867FD, Game.dat 0x68694D |
| What it is | The non-shared branch of the inlined `startPowerRecharge()` (`:115-117`). It ends with `mov [edi+4],eax` at 0x686819, which sets `m_availableOnFrame = frame + ReloadTime`. |
| Span | 6 bytes, 1 instruction: `8B 0D <abs32>` mov ecx,[&TheGameLogic] |
| Stolen bytes | modded `8B 0D 64 BB A2 00` · Game.dat `8B 0D EC BB A2 00` (absolute; copy verbatim per build) |
| Return to | +6: modded 0x686803, Game.dat 0x686953 |
| Skip target | modded 0x68681C, Game.dat 0x68696C (`xor ebp,ebp`, the join both branches already use; the shared branch jumps there from 0x6867FB) |
| State at the site | ebp = controlling Player* (non-NULL, tested at 0x6867DA). edi = interface sub-object. esi = module. |
| Free registers | eax, ecx, edx, ebx (all overwritten before use on the original path). Preserve ebp, edi, esi. |
| Branches into the span | Only the `je` at 0x6867EE, which targets the span start. Nothing targets the middle of the span. |
| Local-player compare | `ebp == local` → `jmp SKIP`. `m_availableOnFrame` stays 0, so the power is ready at once. |

Sites 4 and 5 together cover both creation paths. An object built under construction skips this branch and gets its first charge through the vtable call to site 4. An object created already complete (factory unit, map-placed or OCL-spawned) goes through this site (`05` "Evidence challenge").

### Power: Player::onPowerBrownOutChange and its inlined copy

**Recommended set: 6a plus 6b. 6c is needed only when instant build is off.**

The gameplay effects of low power are the radar loss and `DISABLED_UNDERPOWERED` on KINDOF_POWERED objects. Both come from one routine (`Player.cpp:3264-3273`, via `doPowerDisable`, `:3249-3259`). Every `hasSufficientPower` copy inlined into Energy only computes that routine's argument. Forcing the argument to FALSE for the local player in both places where the routine runs therefore removes every gameplay effect.

#### 6a. Player::onPowerBrownOutChange (not inlined; 21 direct callers)

| Field | Value |
| --- | --- |
| Function | modded 0x456A60, Game.dat 0x456820. Signature (new, closes `04` #10): `8A 44 24 04 84 C0 56 57 8B F9 74 07 E8 ?? ?? ?? ?? EB 05 E8 ?? ?? ?? ?? 8B 87 A0 01 00 00 8B 30 3B F0 74 1C` |
| Span | 6 bytes, 2 whole instructions: `8A 44 24 04` mov al,[esp+4] · `84 C0` test al,al |
| Position-independent | Yes |
| Return to | +6: modded 0x456A66, Game.dat 0x456826. The ZF from the stolen `test` is read by the `je` at +0xA, after `push esi; push edi; mov edi,ecx` (none writes flags), so the cave must run the stolen `test` last. |
| Convention | thiscall `void (Bool brownOut)`, `ret 4`. ecx = Player and `[esp+4]` = brownOut (byte). |
| Free registers | eax, edx. Preserve ecx (read at +8). |
| Local-player compare | `ecx == local` → `mov byte [esp+4],0`, then the stolen bytes, then `jmp RET` |
| Callers covered | 20 in Energy (modded 0x5772A7–0x5775C2; each preceded by an inlined `hasSufficientPower`: `addProduction`, `addConsumption`, `adjustPower`, `objectEntering/LeavingInfluence`, power bonus) plus 0x6808DA (power-plant sabotage: writes `player+0x8C`, then calls with TRUE). Game.dat also has 21. |

#### 6b. Inlined copy in Player::update (sabotage expiry, `Player.cpp:730-734`)

| Field | Value |
| --- | --- |
| Context | modded 0x45118E `call hasSufficientPower` → 0x451193 site → radar calls 0x4511A1/0x4511A8 → `push doPowerDisable (0x456A20)` at 0x4511D7. This is the only other `push doPowerDisable` in the binary besides 0x456A8C inside 6a, so there is no third copy. |
| Site signature | `E8 ?? ?? ?? ?? F6 D8 1A C0 FE C0 88 44 24 13 8B CD 74 07` +5 → modded 0x451193, Game.dat 0x450F53 |
| Span | 6 bytes, 3 whole instructions: `F6 D8` neg al · `1A C0` sbb al,al · `FE C0` inc al (al = !hasSufficientPower; ZF = (al == 0)) |
| Position-independent | Yes |
| Return to | +6: modded 0x451199, Game.dat 0x450F59 (`mov [esp+0x13],al`: the brownOut byte `doPowerDisable` reads; the `je` at +0xC reads ZF) |
| State | ebp = Player (`mov ecx,ebp` before the radar calls) |
| Free registers | ecx, edx. Keep eax intact until the original branch has run its `neg`. |
| Local-player compare | `ebp == local` → `xor al,al` (sets ZF=1, so the enable-radar branch runs, which matches al=0), then `jmp RET`; otherwise the stolen bytes, then `jmp RET` |

#### 6c. Optional: Energy::getEnergySupplyRatio (build-speed penalty)

| Field | Value |
| --- | --- |
| Function | modded 0x577200, Game.dat 0x576F20. Signature: `51 A1 ?? ?? ?? ?? 8B 50 3C 3B 51 0C 73 08` |
| Span | 6 bytes, 2 whole instructions: `51` push ecx · `A1 <abs32>` mov eax,[&TheGameLogic] (absolute; copy verbatim per build) |
| Return to | +6: modded 0x577206, Game.dat 0x576F26 |
| Convention | thiscall `Real ()`, returns in ST0, plain `ret`. ecx = Energy (Player+0x80). The owner Player* is `[ecx+0x10]` (Player+0x90, `02`). |
| Local-player compare | `[ecx+0x10] == local` → `fld1; ret` |
| Callers | 2 in both builds: calcTimeToBuild 0x58C91E and 0x5250DD (probably `ScriptConditions.cpp:1863`; not proven) |

Not patched on purpose: the 6 callers of the non-inlined `hasSufficientPower` (0x45118E, which is 6b, plus 0x513C4A, 0x5280B1, 0x52810E, 0x5758D9 and 0x57BEAF). By source they are read-only consumers: the Eva low-power line (`Eva.cpp:446`), the script condition (`ScriptConditions.cpp:1319`), the skirmish AI (`AISkirmishPlayer.cpp:113`) and academy stats (`AcademyStats.cpp:335`). The result is that EVA can still announce "low power" and the power bar still shows the deficit. Gameplay is unaffected.

Lifecycle edges for the implementer:

- Arming while already browned out leaves objects disabled until the next power event calls 6a.
- Disarming while in deficit leaves them enabled until the next power event.
- The trainer must not call the handler itself from a foreign thread (`04` #9).

## Player offsets

All three are **PROVEN** in both builds. Player::init's site signature `C7 46 30 01 00 00 00 8A 87 ?? ?? 00 00 88 86 ?? ?? 00 00 88 86 ?? ?? 00 00` matches modded 0x450433 and Game.dat 0x450203, with identical bytes including the offsets.

| Member | Offset | Type | Proof (modded.exe) | Source |
| --- | --- | --- | --- | --- |
| `m_playerType` | **+0x30** | `Int` enum (HUMAN 0, COMPUTER 1) | 0x450433 `mov dword [esi+0x30],1` (template branch) and 0x4504E1 `mov dword [esi+0x30],1` (neutral branch). It sits next to `m_money` (Money vtable at +0x34, `Player.h:746-747`). | `Player.cpp:473`, `:494` `m_playerType = PLAYER_COMPUTER;` |
| `m_observer` | **+0x262** | `Bool` (byte) | 0x45043A `mov al,[edi+0xB8]` (`pt->isObserver()`) → 0x450440 `mov [esi+0x262],al`. Accessor `isPlayerObserver` at 0x457E70 `8A 81 62 02 00 00 C3`. | `Player.cpp:474`, `:3943-3946` |
| `m_isPlayerDead` | **+0x448** | `Bool` (byte) | 0x450446 `mov [esi+0x448],al` (same al: "observers are dead"). Accessor `isPlayerActive` at 0x457E80 (Game.dat 0x457C40): `mov al,[ecx+0x262]; test; jne; mov al,[ecx+0x448]; test; jne; mov eax,1; ret`. | `Player.cpp:475`, `:3955-3958` `!m_observer && !m_isPlayerDead` |

Guard reads, with `local = *(u32*)(*(u32*)PL + 0xC)`: `u8[local+0x262] == 0`, `u8[local+0x448] == 0` and `i32[local+0x30] == 0` (human). An observer has both bytes set from init. Read-site counts are equal in both builds (4 reads of +0x262 and 7 of +0x448 in each), which is consistent with one shared layout.

## Build differences

Method: each function was located in both files by its signature, then disassembled with objdump. The instruction streams were normalized: internal branch targets became function-relative, external call/jump targets became `EXT`, and absolute data operands (0x93xxxx–0xA7xxxx) became `ABS`. The streams were then compared instruction by instruction.

| Function | modded.exe | Game.dat | Length (both) | Instructions | Normalized differences |
| --- | --- | --- | --- | --- | --- |
| AIUpdateInterface::privateMoveAwayFromUnit | 0x5D4BE0 | 0x5D49D0 | 0x23F | 178 / 178 | 0 |
| ThingTemplate::calcTimeToBuild | 0x58C8A0 | 0x58C650 | 0x1E2 | 143 / 143 | 0 |
| UpgradeTemplate::calcTimeToBuild | 0x4AEAF0 | 0x4AEC30 | 0x11 | 4 / 4 | 0 |
| SpecialPowerModule::startPowerRecharge | 0x686E10 | 0x686F60 | 0x8F | 62 / 62 | 0 |
| SpecialPowerModule ctor (site 0x6867FD) | 0x686720 | 0x686870 | 0x1F5 | 156 / 156 | 1: `push` of the SEH handler (0x919490 → 0x919910), a relocated `.text` address |
| Player::onPowerBrownOutChange | 0x456A60 | 0x456820 | 0x45 | 24 / 24 | 1: `push doPowerDisable` (0x456A20 → 0x4567E0, the same −0x240 delta as the function) |
| Energy::getEnergySupplyRatio | 0x577200 | 0x576F20 | 0x2A | 16 / 16 | 0 |
| Player::update inline site (6b) | 0x451193 | 0x450F53 | — | site signature only | 19-byte site signature identical apart from the call rel32 |
| Player::init offsets site | 0x450433 | 0x450203 | — | site signature only | 25-byte site identical, offsets included |

**Conclusion:** p0ls3r's edits do not touch any of these functions. The only differences are relocations caused by the shifted link layout (`.text` is 4.3% byte-identical at equal VAs, `01`). The stolen bytes are identical in both builds, except the two absolute operands named in the site tables (`fmul [30.0f]` and `mov ecx,[&TheGameLogic]`).

## Unproven

| Item | Why it is open | Runtime read / check that settles it |
| --- | --- | --- |
| Live values of `m_gameMode` per mode | The offset is proven; the values per mode are source-derived. | Spike step 2: read `*(*TheGameLogic+0x94)` in skirmish (2), replay (3), shell (4) and between games (6). |
| Whether the stack slot `[esp+0x2C]` at 0x4954F8 is the `gameMode` argument of `prepareNewGame` | The prologue was not walked. The constructor store proves the offset on its own. | None needed for the guard. |
| Caller 0x5250DD of `getEnergySupplyRatio`, and the mapping of the five other `hasSufficientPower` callers to Eva / scripts / AI / academy | Mapped by source only. | A breakpoint-free check is enough: hit counters in a 6c cave during skirmish. |
| Owner of the brown-out call at 0x6808DA | Inferred as power-plant sabotage from its shape (writes `player+0x8C` = frame + duration, then calls with TRUE). | Covered by 6a either way. |
| Behaviour of the caves | Static spans and register state are proven; nothing has executed. | Live checks C1–C4 (build), T3/T4 (cooldown, including a factory-built unit with an ability for site 5), a power-deficit test with a sabotage expiry for 6b, and the O13 stress test. |
| RoTR/EPA build or recharge paths outside these functions | The `.big` INI files were not read. | Static `.big` scan, then T4/C2 (`05` Unknowns). |
| Arm/disarm power edges | Engine-lifecycle behaviour (see 6). | Arm while in deficit; confirm objects re-enable on the next power event. |
| `m_specialPowerReadyTimerList` offset | Not needed: site 4 replaces the timer-list write. | Spike step 5, only if the write approach is revived. |

## Verdict

| Patch | Verdict | Reason |
| --- | --- | --- |
| Online/replay guard (`m_gameMode` +0x94) | **READY** | Proven by the constructor store `+0x94=GAME_NONE, +0x98=1000` and the `setGameMode` store; signatures resolve once per build. This closes the static half of `04` #2. Kai's second signal (no UDP/TCP endpoints) is still required. |
| Observer / dead / player-type guards | **READY** | +0x262, +0x448 and +0x30 are proven by `Player::init` stores and the `isPlayerActive` accessor in both builds. |
| a. Crash fix (privateMoveAwayFromUnit) | **READY** | 7-byte whole-instruction span at 0x5D4C1F / 0x5D4A0F with position-independent stolen bytes; the cave re-creates ZF for the `je`. The exit at 0x5D4E17 / 0x5D4C07 is stack-balanced. No in-place variant fits. |
| b1. Instant build, ThingTemplate::calcTimeToBuild | **READY** | Clean 5-byte prologue, position-independent, no branches into the span, identical in both builds. |
| b2. Instant build, UpgradeTemplate::calcTimeToBuild | **READY** | 9-byte span (fld + fmul) avoids splitting the `fmul`; its absolute operand differs per build and is copied from the target image. |
| c1. No cooldown, startPowerRecharge | **READY** | Clean 5-byte prologue; the player comes from `getControllingPlayer` with ecx saved across the call. |
| c2. No cooldown, constructor inline (0x6867FD) | **READY** | New unique site signature (closes `04` #10 for this target). The 6-byte span holds one absolute operand. The skip target is the existing join 0x68681C. |
| d1. Power, onPowerBrownOutChange (6a) | **READY** | New unique signature (closes `04` #10). The 6-byte span ends with the `test` the `je` reads. Covers all 21 callers. |
| d2. Power, Player::update inline copy (6b) | **READY** | The one other brown-out path. 6-byte span; `xor al,al` gives both the value and the ZF the original produces. Ship it with d1, or the local player can brown out after a sabotage expires. |
| d3. Power, getEnergySupplyRatio (6c) | **READY (optional)** | Needed only when instant build is off. The 6-byte span holds one absolute operand. |
| "Never low power" for EVA and the power bar | **NOT READY (by design)** | Read-only `hasSufficientPower` consumers (6 callers) are not patched. EVA and the bar still show the real deficit; gameplay is unaffected. Patching them would add 5 more sites for a cosmetic gain. |

All READY items are static specifications. Each still needs its live check in `05` before it ships, and the patching discipline in `04` #1 and #4 (suspend, write, read back, flush) applies to every site.
