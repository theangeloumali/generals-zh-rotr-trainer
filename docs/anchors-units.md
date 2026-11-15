# Unit anchors: static proof for value-write unit features

Static analysis notes. Static only: nothing was launched or attached. Inputs are the same two binaries as `docs/anchors.md` (`modded.exe` and `Game.dat`, ZH 1.04 code, image base 0x400000, no `.reloc`). Source: EA GPL `GeneralsMD/Code/GameEngine` (paths below are relative to it unless they start with `GameEngineDevice/`). Tools: `objdump -M intel` on address ranges and python3 byte scans over `.text`/`.rdata`. Scratch: `scratchpad/units/{pe.py,bodyvt.py,bodyall.py,dis.sh}`.

Labels: **PROVEN** means an accessor or store at the cited VA in both builds that only the cited source line explains. **LAYOUT** means the offset follows from declaration order between two PROVEN neighbours; it is listed again under `## Unproven` with the runtime read that settles it.

Every cited instruction run was byte-compared between the builds. The only differences are absolute operands (`&TheGameLogic`) and `call rel32` targets, so offsets are identical in both builds.

Per-build constants added by this slice (the ones in `docs/anchors.md` still apply):

| Constant | modded.exe | Game.dat | Proof |
| --- | --- | --- | --- |
| `&TheGameEngine` | 0xA29B00 | 0xA29B80 | `A1 <GD> 8B 40 18 8B 0D <GE> 8B 11 50 FF 52 20 A1 <GD> C6 40 14 01` at M 0x5FA6BC / G 0x5FA67C: `TheGameEngine->setFramesPerSecondLimit(TheGlobalData->m_framesPerSecondLimit); TheWritableGlobalData->m_useFpsLimit = true` (`GameLogic/System/GameLogicDispatch.cpp:433-438`) |
| `&TheGlobalData` (same object as `TheWritableGlobalData`) | 0xA2A21C | 0xA2A2A4 | same signature (`<GD>`), and the frame limiter at 0x40FE68 in both builds |
| Body interface vtables | see `## Health` | see `## Health` | `.rdata` scan + constructors |

Object offsets used throughout (from `docs/anchors.md`/`GameAddresses.cs`, re-proven here where marked):

| Object field | Offset | Status here |
| --- | --- | --- |
| `m_body` (BodyModuleInterface*) | +0x174 | PROVEN: `mov ecx,[esi+0x174]; test ecx,ecx; je; mov edx,[ecx]; push eax; push ebp; push ebx; call [edx+0x38]` at M 0x549E72 / G 0x549C42, which is `body->onVeterancyLevelChanged(old,new,feedback)` (`Object/Object.cpp:3096-3098`, interface slot 14) |
| `m_experienceTracker` | +0x18C | PROVEN: `Object::getVeterancyLevel` M 0x549AA0 / G 0x549870 `8B 81 8C 01 00 00 85 C0 74 04 8B 40 0C C3` (`Object.cpp:2969-2972`) |
| `m_weaponSet` (embedded WeaponSet) | +0x1D0 | PROVEN: `lea ecx,[edx+0x1D0]` in `setWeaponBonusCondition` (below) and `lea ecx,[edi+0x1D0]` before `isReloadTimeShared` in `Weapon::reloadWithBonus` (M 0x4C6B20) |
| `m_weaponSet.m_weapons[3]` | +0x1D8, +0x1DC, +0x1E0 | PROVEN (below) |
| `m_weaponSet.m_curWeapon` | +0x1E4 | PROVEN (below) |
| `m_weaponBonusCondition` | +0x204 | PROVEN (below) |

## Health

**Path:** `B = [obj + 0x174]`. B is the **BodyModuleInterface sub-object**, not the module base: module base = B − 0x14 (ObjectModule = MemoryPoolObject vptr +0, Snapshot vptr +4, `m_moduleData` +8, `m_object` +0xC, BehaviorModuleInterface vptr +0x10, BodyModuleInterface vptr +0x14). Under MSVC every BodyModuleInterface override receives this same adjusted pointer, so the getters below index B directly.

| Field (relative to B) | Offset | Status | Evidence (M / G, identical bytes) | Source |
| --- | --- | --- | --- | --- |
| interface vtable | +0x00 | PROVEN | `call [edx+0x38]` through `[B]` at M 0x549E8A | `Include/GameLogic/Module/BodyModule.h:119-198` (36 pure virtuals; slot order used below) |
| `ObjectModule::m_object` | −0x08 | PROVEN | `ActiveBody::setIndestructible` M 0x684230 / G 0x684380: `mov [ecx+0x87],bl; mov esi,[ecx-0x8]; push 0x13; mov ecx,esi; call` (getObject then the bridge `isKindOf` test) | `Object/Body/ActiveBody.cpp:1374-1381` |
| `Module::m_moduleData` | −0x0C | PROVEN | slot 7 `getSubdualDamageHealRate`: `8B 41 F4 8B 40 14` (`[[B-0xC]+0x14]`) in every ActiveBody-family vtable | `ActiveBody.h:58` |
| `m_damageScalar` (Real) | +0x04 | PROVEN | slot 30 `getDamageScalar` M 0x8E10C0 / G 0x4424A0 `D9 41 04 C3` | `BodyModule.h:280-281,302` |
| `m_currentHealth` (Real) | +0x08 | PROVEN | slot 3 `getHealth` M 0x6841C0 / G 0x684300 `D9 41 08 C3` | `ActiveBody.cpp:1323-1325` |
| `m_prevHealth` (Real) | +0x0C | PROVEN | slot 6 `getPreviousHealth` M 0x682620 / G 0x682760 `D9 41 0C C3` | `ActiveBody.h:116` |
| `m_maxHealth` (Real) | +0x10 | PROVEN | slot 4 `getMaxHealth` M 0x6841D0 / G 0x684320 `D9 41 10 C3` | `ActiveBody.cpp:1337` |
| `m_initialHealth` (Real) | +0x14 | PROVEN | slot 5 `getInitialHealth` M 0x684220 / G 0x684370 `D9 41 14 C3` | `ActiveBody.cpp:1365` |
| `m_currentSubdualDamage` (Real) | +0x18 | LAYOUT | between +0x14 and +0x1C | `ActiveBody.h:152` |
| `m_curDamageState` | +0x1C | PROVEN | slot 11 `getDamageState` M 0x4FA650 (COMDAT-folded) / G 0x684310 `8B 41 1C C3` | `ActiveBody.h:154` |
| `m_lastDamageTimestamp` | +0x7C | PROVEN | slot 19 M 0x6824E0 / G 0x682610 `8B 41 7C C3` | `ActiveBody.h:93` |
| `m_indestructible` (Bool) | +0x87 | PROVEN | slot 33 `isIndestructible` M 0x682630 / G 0x682770 `8A 81 87 00 00 00 C3`; slot 32 stores it (above) | `ActiveBody.h:119,165` |

Module size is 0xB0. `StructureBody::m_constructorObjectID` and `UndeadBody::m_isSecondLife` both sit at module+0xB0 (B+0x9C); their constructors store it (table below).

### Body classes, told apart by the interface vtable (`[B]`)

Found by scanning `.rdata` for 36-slot tables whose slot 30 is `D9 41 04 C3`. There are exactly eight per build, the seven body classes plus the abstract base. Module classes are compiled into the exe. RoTR INI can only choose among these classes, so **custom body types are impossible**. RoTR can still assign Highlander/Immortal/Undead/Hive bodies to units that vanilla does not.

| Class | modded.exe | Game.dat | How it is identified |
| --- | --- | --- | --- |
| ActiveBody | 0x959AA0 | 0x959AA8 | Getters above; slot 0 `attemptDamage` M 0x682B00 / G 0x682C40; ctor stores it at M 0x6823CC |
| HighlanderBody | 0x959CC8 | 0x959CD0 | Only slot 0 differs (M 0x684AA0 / G 0x684BF0); ctor M 0x684998 calls the ActiveBody ctor (0x6822B0) |
| ImmortalBody | 0x959E28 | 0x959E30 | Only slot 31 `internalChangeHealth` differs (M 0x684C40 / G 0x684D60) |
| StructureBody | 0x959F88 | 0x959F90 | Same interface slots as ActiveBody; ctor M 0x684CB8 / G 0x684E08 `C7 46 14 <vt> C7 86 B0 00 00 00 00 00 00 00` (`m_constructorObjectID = INVALID_ID`) |
| HiveStructureBody | 0x95A108 | 0x95A110 | Slot 0 differs (M 0x684F80 / G 0x6850D0); ctor M 0x684E78 calls the StructureBody ctor (0x684C90) |
| UndeadBody | 0x95A2A8 | 0x95A2B0 | Slot 0 differs (M 0x685310 / G 0x685450); ctor M 0x685208 / G 0x685348 `C7 46 14 <vt> C6 86 B0 00 00 00 00` (`m_isSecondLife = false`) |
| InactiveBody | 0x9597A4 | 0x9597AC | Own `getHealth` M 0x682210 / G 0x682350 `fld [0x939DA4]` (0.0f) — **no health fields**; B+0x08 is `m_dieCalled`. Never write. |
| BodyModule (abstract) | 0x959984 | 0x95998C | Never on a live object |

Only the six ActiveBody-family vtables have the field layout above. An object whose `[B]` is not one of those six, including NULL B, is skipped.

### What a bare write does

- **Writing `m_currentHealth` (B+0x08).** The value takes effect for the next damage or heal, and `getHealth()` returns it immediately. The health bar is computed at draw time from `getHealth()/getMaxHealth()` (source-read, not traced in the binary). Writing it skips `setCorrectDamageState()`, `evaluateVisualCondition()` and `setEffectivelyDead()`, which run only inside `internalChangeHealth` (`ActiveBody.cpp:1212-1250`). So the damage state (B+0x1C) and the damaged/smoking model condition stay stale until the next health change crosses a threshold. Do not write B+0x1C to compensate. The model flags would still disagree, and the next `internalChangeHealth` recomputes it anyway.
- **Never write ≤ 0.** Death fires only on the transition `m_currentHealth <= 0 && m_prevHealth > 0` (`ActiveBody.cpp:665`), and `internalChangeHealth` copies current into prev first (`:1215`). A unit written to 0 has prev = 0 on every later hit, so it never dies. That includes `kill()`, whose `m_kill` path sets `amount = m_currentHealth` = 0 (`:530-533`). The result is an unkillable zombie. "Weak enemies" must floor at a positive value (1.0f).
- **Never write `m_maxHealth` (B+0x10).** Veterancy (`ActiveBody.cpp:1471-1474`, `setMaxHealth(m_maxHealth*mult, PRESERVE_RATIO)`) and UndeadBody's second life rescale it. An external value would compound with the engine's.
- **Holding health at max is not god mode.** Damage is `internalChangeHealth(-amount)` clamped at 0 (`:1218-1228`). One hit larger than max health kills from full: superweapons, crush, `m_kill` weapons and script kills (`amount = m_currentHealth`). So do several hits landing in one logic frame before the trainer's next poll.
- **`m_indestructible` (B+0x87) is the real god mode.** `ActiveBody::attemptDamage` returns before any change when it is set (`ActiveBody.cpp:353-354`). That covers `kill()` (the `m_kill` path is inside attemptDamage). Highlander/Undead/Hive overrides all end in `ActiveBody::attemptDamage` (`HighlanderBody.cpp:60`, `UndeadBody.cpp:92`). Healing is not blocked. Caveats:
  1. Map scripts and bridges set this flag too (`setIndestructible`, bridges mirror it to towers, `:1379-1395`). Record the original byte per object and restore it, never blindly write 0.
  2. Removal paths that never call attemptDamage still remove the object: sell, script delete, `destroyObject`.
- **`m_damageScalar` (B+0x04)** multiplies every non-`UNRESISTABLE`, non-status, non-subdual hit (`ActiveBody.cpp:514-520`). The engine stacks legitimate modifiers into it multiplicatively (`applyDamageScalar`, `BodyModule.h:280`; e.g. Hold-the-Line). Writing it overwrites them, so save and restore per object. It does not affect `m_kill`. This is a cleaner "weak enemies" lever than writing health (e.g. 4.0 on enemy units) and needs no zombie guard.
- **Subclasses:**
  - HighlanderBody clamps resistible damage to leave 1 HP (`HighlanderBody.cpp:57-58`), and ImmortalBody floors health at 1 (`ImmortalBody.cpp:58`). A "weak enemy" of either class stays at 1 HP by design.
  - UndeadBody starts a second life on the lethal hit and changes max health (`UndeadBody.cpp:82-90`).
  - StructureBody and HiveStructureBody use the ActiveBody fields unchanged. A Hive may route damage to its slaves instead.

Recipes (all on ActiveBody-family B only, alive objects only):

| Feature | Write | Restore |
| --- | --- | --- |
| God mode (own units) | B+0x87 ← 1 | original byte per object |
| Instant heal (own units) | B+0x08 ← [B+0x10] when current < max and current > 0 | none needed |
| Weak enemies (preferred) | B+0x04 ← chosen multiplier (e.g. 4.0f) | original float per object |
| Weak enemies (alternative) | B+0x08 ← max(1.0f, [B+0x10] × fraction) | none |

## Veterancy

**Path:** `T = [obj + 0x18C]` (ExperienceTracker, MemoryPoolObject + Snapshot, so members start at +0x08). Every Object creates one in its constructor (`Object.cpp:476`). Check it anyway.

| Field (relative to T) | Offset | Status | Evidence | Source |
| --- | --- | --- | --- | --- |
| `m_parent` (Object*) | +0x08 | PROVEN | `ExperienceTracker::setVeterancyLevel` M 0x61B7E0 / G 0x61B860: `mov ecx,[esi+0x8]` passed to `getTemplate` and to `onVeterancyLevelChanged` | `Object/ExperienceTracker.cpp:106-119` |
| `m_currentLevel` (0..3) | +0x0C | PROVEN | same function: `mov ebx,[esi+0xC]; cmp ebx,edi; je; mov [esi+0xC],edi`; also `getVeterancyLevel` | `ExperienceTracker.cpp:111-114` |
| `m_currentExperience` (Int) | +0x10 | PROVEN | same function: `mov eax,[eax+edi*4+0x14C]; mov [esi+0x10],eax` (`= getTemplate()->getExperienceRequired(level)`) | `ExperienceTracker.cpp:115` |
| `m_experienceSink` (ObjectID) | +0x14 | LAYOUT | | `ExperienceTracker.h:74` |
| `m_experienceScalar` (Real) | +0x18 | LAYOUT | | `ExperienceTracker.h:75` |
| ThingTemplate experience required[4] | template+0x14C | PROVEN as an operand (the Object→template pointer is not mapped; see `## Unproven`) | | |

Signature (unique in both builds): `53 56 8B F1 8B 5E 0C 57 8B 7C 24 10 3B DF 74 28 8B 4E 08 89 7E 0C E8 ?? ?? ?? ?? 8B 4E 08 85 C9 8B 84 B8 4C 01 00 00 89 46 10`.

**What the engine does on a level change.** `Object::onVeterancyLevelChanged` (M 0x549E00, `Object.cpp:3088-3165`):

1. Updates upgrade modules and grants the veterancy upgrade.
2. Calls `body->onVeterancyLevelChanged`. That multiplies max health by `GlobalData::m_healthBonus[new]/[old]` and swaps the ARMORSET_VETERAN/ELITE/HERO flags (`ActiveBody.cpp:1412-1500`).
3. Sets and clears WEAPONSET_* flags and WEAPONBONUSCONDITION_* bits.
4. Plays the promotion animation and sound.

**Weapon bonus bits: `obj + 0x204` (`m_weaponBonusCondition`, PROVEN).**

- `Object::setWeaponBonusCondition` at M 0x54CD90 / G 0x54CB60: `mov edx,ecx; mov ecx,[esp+4]; mov eax,1; shl eax,cl; push esi; mov esi,[edx+0x204]; or eax,esi; ...; lea ecx,[edx+0x1D0]; call weaponSetOnWeaponBonusChange` (`Object.cpp:4674-4684`). The clear twin is at M 0x54CDC0 / G 0x54CB90.
- Bit indices are PROVEN by the LEVEL_ELITE case at M 0x54A14E: `push 9; call clear` · `push 0xA; call set` · `push 0xB; call clear`. That gives **VETERAN = bit 9 (0x200), ELITE = bit 10 (0x400), HERO = bit 11 (0x800)** (`Weapon.h:177-191`, `Object.cpp:3132-3137`).
- Damage, range and rate-of-fire bonuses are computed at fire time from this field, so a direct write takes effect on the next shot. The skipped `weaponSetOnWeaponBonusChange` only rescales a reload already in progress.

**What a bare `m_currentLevel` write does:**

- It changes the chevron. `Drawable.cpp:3808` reads `getVeterancyLevel()` each draw.
- It changes the experience a killer earns, because `getExperienceValue` uses the level.
- It does **not** change damage, health or armor. Those come from the bonus bits, the max-health scale and the armor set flags.
- It is undone at the unit's next experience gain. `addExperiencePoints` recomputes the level from `m_currentExperience` (`ExperienceTracker.cpp:172-189`). With the experience still low, the unit is demoted and the bonuses are cleared through the normal callback.

**Recommended value-write recipe** (own units, trainable or not):

1. **Experience:** `T+0x10 ← 0x0FFFFFFF`, when `T+0x14` (sink) is 0. Do not touch the level. On the unit's next experience gain the engine walks the levels (`:175-181`), lands on HEROIC and runs the full `onVeterancyLevelChanged`: health scale, armor, weapon set, upgrade, chevron. `addExperiencePoints` returns early for non-trainable templates (`:162-163`) and for sinks (`:150-159`), so step 2 is the only effect on those.
2. **Bonus bits for an immediate combat bonus:** `obj+0x204 ← (v & ~0x600) | 0x800`. When step 1 later fires, the engine sets the same bits itself, so the two never conflict.
3. **Optional, cosmetic:** `T+0x0C ← 3` for an immediate chevron. Cost: the later engine promotion then sees old == new and never runs, so the max-health and armor bonuses are lost. Offer this only as a separate toggle.

## Ammo

**Path:** `W = [obj + 0x1D8 + 4*slot]`, slot 0..2 (PRIMARY, SECONDARY, TERTIARY), may be NULL. The current slot is `[obj + 0x1E4]`.

- **Object weapon array: PROVEN.** `Object::getCurrentWeapon` body M 0x547967-0x54797C / G 0x547737-0x54774C: `mov edx,[ecx+0x1E4]; mov [eax],edx; mov eax,[ecx+0x1E4]; mov eax,[ecx+eax*4+0x1D8]; ret 4` (`Object.cpp:1397-1405`, WeaponSet = Snapshot vptr +0, `m_curWeaponTemplateSet` +4, `m_weapons[3]` +8, `m_curWeapon` +0x14; `WeaponSet.h:198-205`).

| Field (relative to W; Weapon = MemoryPoolObject + Snapshot) | Offset | Status | Evidence (M / G) | Source |
| --- | --- | --- | --- | --- |
| `m_template` (const WeaponTemplate*) | +0x08 | PROVEN | `mov ecx,[esi+0x8]; mov eax,[ecx+0x164]` in reloadWithBonus M 0x4C6B33 / G 0x4C6703 | `Weapon.cpp:1903-1908` |
| `m_wslot` | +0x0C | LAYOUT | | `Weapon.h:791` |
| `m_status` | +0x10 | PROVEN | `Weapon::getStatus` M 0x4C8A10 / G 0x4C85B0 stores 0/1 there | `Weapon.cpp:2760-2776` |
| `m_ammoInClip` (UnsignedInt) | +0x14 | PROVEN | getStatus `mov eax,[ecx+0x14]; test; jbe`; reloadWithBonus `mov [esi+0x14],eax` / `mov dword [esi+0x14],0x7FFFFFFF` | `Weapon.cpp:1908-1910,2769` |
| `m_whenWeCanFireAgain` (frame) | +0x18 | PROVEN | getStatus `cmp eax,[ecx+0x18]` with eax = `[TheGameLogic]+0x3C` | `Weapon.cpp:2767` |
| `m_whenPreAttackFinished` | +0x1C | PROVEN | getStatus `cmp eax,[ecx+0x1C]; jae; mov eax,4; ret` (PRE_ATTACK) | `Weapon.cpp:2763-2766` |
| `m_whenLastReloadStarted` | +0x20 | LAYOUT | | `Weapon.h:796` |
| WeaponTemplate clip size (Int, 0 = unlimited) | tmpl+0x164 | PROVEN | reloadWithBonus (above) | `Weapon.cpp:1908` |
| WeaponTemplate clip reload time | tmpl+0x168 | PROVEN as an operand (`fild [ecx+0x168]` on the reload-time path) | | `Weapon.cpp:1913` |

Status values (from the stores and returns in getStatus): READY_TO_FIRE 0, OUT_OF_AMMO 1, BETWEEN_FIRING_SHOTS 2, RELOADING_CLIP 3, PRE_ATTACK 4. **`m_status` is recomputed from ammo and frame on every `getStatus()` call** (`Weapon.cpp:2767-2774`), so the trainer never writes it.

How reload works:

- Each shot does `--m_ammoInClip` (`Weapon.cpp:2642`).
- At 0, an AutoReloadsClip weapon reloads after the clip-reload delay (`:2653-2656`).
- A weapon with **AutoReloadsClip = No** (aircraft that must land, e.g. jets) gets `m_status = OUT_OF_AMMO; m_whenWeCanFireAgain = 0x7FFFFFFF` (`:2659-2661`). It stays empty until an airfield calls `setClipPercentFull`/`reloadAmmo`.
- Clip size 0 means unlimited: `m_ammoInClip = 0x7FFFFFFF` (`:1909-1910`).

**Infinite-ammo recipe.** For each non-NULL W with `clip = [tmpl+0x164] > 0`:

1. If `[W+0x14] < clip`, write `W+0x14 ← clip`.
2. If `[W+0x18] == 0x7FFFFFFF`, also write `W+0x18 ← [TheGameLogic]+0x3C` (current frame). Otherwise the weapon stays OUT_OF_AMMO even with a full clip, because getStatus only re-evaluates when `now >= m_whenWeCanFireAgain`.

Optional no-reload: `W+0x18 ← frame` while the computed status would be RELOADING_CLIP. This also skips the between-shot delay, a rate-of-fire cheat; keep it a separate toggle.

Caveats:

- A jet that fires its whole clip inside one trainer poll interval still empties. Its AI may already have chosen to return to base; the jet AI was not read (`## Unproven`).
- Objects with `isReloadTimeShared` copy one weapon's next-shot frame to the others (`Weapon.cpp:1922-1930`). Refilling every slot handles that.

## Fog of war

How it is stored (`Include/GameLogic/PartitionManager.h:296-308,1231-1249`):

- ThePartitionManager owns `PartitionCell* m_cells` (`m_cellCountX × m_cellCountY`).
- Each cell has `ShroudLevel m_shroudLevel[16]`, one per player index, with `m_currentShroud` and `m_activeShroudLevel`.
- Cell status for a player (`Object/PartitionManager.cpp:1382-1392`): `m_currentShroud == 1` shrouded, `== 0` fogged, `< 0` clear. It is a looker reference count: `addLooker` decrements, `removeLooker` increments.

How the engine reveals:

- `PartitionManager::revealMapForPlayer` calls `addLooker` then `removeLooker` on every cell (`:2972-2981`). The permanent variant leaves the looker in place (`:2986-2995`).
- The visible effect comes from the **edge trigger inside addLooker/removeLooker**, not from the stored value. On a status change it calls `invalidateShroudedStatusForAllCois`, and for the local player `TheDisplay->setShroudLevel(x,y,…)` and `TheRadar->setShroudLevel(x,y,…)` (`:1288-1297`). Those calls write the terrain shroud texture (`GameEngineDevice/…/W3DShroud.cpp:286-310`) and the radar.

Why a value write does not reveal the map:

- Writing `m_currentShroud = -N` for the local player changes only logic state. The terrain texture and radar stay black. Cached object shroudedness is not invalidated, so enemy units stay hidden.
- With N large, no later `removeLooker` ever returns the cell to fogged, so the texture is never refreshed for those cells.
- The only global switch, `GlobalData::m_shroudOn`, is read only inside `#if defined(_DEBUG) || defined(_INTERNAL)` blocks (`GameEngineDevice/Source/W3DDevice/GameClient/W3DView.cpp:995-999`, `W3DScene.cpp:108-114` and `:792-795`, `BaseHeightMap.cpp:316-322`, `Common/System/W3DRadar.cpp:1258-1261`). Retail builds compile the `#else` branch, which ignores it. `m_fogOfWarOn` is likewise debug-only shading.

**Plainly: map reveal is not feasible as a value write.** It needs an engine call (`revealMapForPlayer` or per-cell `setShroudLevel`), which is code execution and is out of bounds on the owner's machine. ThePartitionManager's address and the PartitionCell layout were therefore not mapped. One speculative client-buffer route is listed under `## Unproven`.

## Game speed

| Value | Address | Status | Evidence | Source |
| --- | --- | --- | --- | --- |
| `GameEngine::m_maxFPS` (Int) | `[&TheGameEngine] + 0x08` | PROVEN | Frame limiter in `GameEngine::execute`, M and G both at 0x40FE8C: `fild dword [ebx+0x8]; mov esi,eax; fdivr dword [0x939FAC] (1000.0f); fsub dword [0x93974C] (1.0f); call ftol` with `ebx = this` (`mov ebx,ecx` in the prologue at 0x40FD84). Setter `mov eax,[esp+4]; mov [ecx+0x8],eax; ret 4` at M 0x40E2E0 is vtable slot 8 (`call [edx+0x20]` at M 0x5FA6CD). | `Common/GameEngine.cpp:241-245, 881-888` |
| `GlobalData::m_useFpsLimit` (Bool) | `[&TheGlobalData] + 0x14` | PROVEN | limiter loop `mov al,[edx+0x14]; test al,al; je` at M 0x40FEAA; set to 1 at game start, M 0x5FA6D5 `C6 40 14 01` | `GameEngine.cpp:882`, `GameLogicDispatch.cpp:438` |
| `GlobalData::m_framesPerSecondLimit` (Int) | `[&TheGlobalData] + 0x18` | PROVEN | `mov eax,[eax+0x18]` passed to the setter at M 0x5FA6C1 | `GlobalData.h:101` |
| `GlobalData::m_TiVOFastMode` | `[&TheGlobalData] + 0x924` | PROVEN | `mov cl,[eax+0x924]` + `cmp [TheGameLogic+0x94],3` at M 0x40FE68-0x40FE84 | `GameEngine.cpp:876` |

How it works:

- In single player, the logic advances exactly one frame per `GameEngine::update` (`GameEngine.cpp:773-775`: `TheNetwork == NULL && !isGamePaused()` → `TheGameLogic->UPDATE()`).
- The loop sleeps until `(now − prevTime) ≥ (DWORD)(1000.0f/m_maxFPS − 1)` ms.
- So **game speed = m_maxFPS / 30**: normal is 30 (`LOGICFRAMES_PER_SECOND`, `GameCommon.h:72`), 60 is 2×, 15 is ½.
- The speed-up is capped by how fast the machine can render. Steps are coarse because the limit is whole milliseconds (60 → 15 ms ≈ 62 fps, 120 → 7 ms ≈ 125 fps).

Safety:

- The engine itself changes `m_maxFPS` at run time: the script actions at `ScriptActions.cpp:6996-7000` and game start at `GameLogicDispatch.cpp:433-437`. A changed value is therefore a state the engine supports.
- Clamp to **1..1000**, the engine's own range (`GameLogicDispatch.cpp:433-434`). **Never write 0.** `1000.0f/0` is +inf, and the `ftol` result is undefined.
- Write only `m_maxFPS`. `m_framesPerSecondLimit` is just the value scripts restore.
- Campaign scripts may reset the speed, so re-assert when the value differs. Restore 30 (or the saved value) on disarm.
- The limiter is bypassed while the tactical view's time multiplier > 1 or script fast-forward is on (`GameEngine.cpp:863`).
- Keep the existing mode guard {0, 2}. In LAN/online, each peer runs its own limiter, so a change there would only make the trainer's machine lag.

## Other players

| Value | Offset | Status | Evidence (M / G, identical bytes) | Source |
| --- | --- | --- | --- | --- |
| PlayerList `m_local` | `[&ThePlayerList]+0x0C` | PROVEN earlier | `docs/anchors.md` | `PlayerList.h:165` |
| PlayerList `m_playerCount` | +0x10 | PROVEN | loop `mov esi,[ecx+0x10]; xor eax,eax; test esi,esi` at M 0x45A7A2 / G 0x45A9E2 | `PlayerList.h:166` |
| PlayerList `m_players[16]` | +0x14 | PROVEN | `PlayerList::getNthPlayer` M 0x45A780 / G 0x45A9C0: `mov eax,[esp+4]; test eax,eax; jl; cmp eax,0x10; jge; mov eax,[ecx+eax*4+0x14]; ret 4` | `PlayerList.h:167`, `MAX_PLAYER_COUNT = 16` (`GameCommon.h:113`) |
| Player `m_playerType` | +0x30 | PROVEN earlier | 0 = PLAYER_HUMAN, 1 = PLAYER_COMPUTER | `GameCommon.h:151-155` |
| Player `m_money.m_money` | +0x38 | PROVEN earlier | "Broke AI" writes this on the AI player | |
| Player `m_ai` (AIPlayer*) | +0x150 | PROVEN | M 0x453060 / G 0x452E20: `mov ecx,[ecx+0x150]; test ecx,ecx; je; mov eax,[ecx]; jmp [eax+0x30]; xor al,al` (a Player forwarder that returns FALSE without an AI) | `Player.h:766` |
| Player `m_playerRelations` | +0x1A4 | PROVEN (adjacent) | `Player::getRelationship` reads `m_teamRelations` at M 0x450AB3 / G 0x450883: `mov eax,[edi+0x1A8]; mov ecx,[eax+0x18]; test ecx,ecx; je` (map `empty()`), then the player map follows | `Player.cpp:574-604`, `Player.h:783-784` |
| Player `m_teamRelations` | +0x1A8 | PROVEN | same | |
| Player observer / dead | +0x262 / +0x448 | PROVEN earlier | | |
| Player `m_playerIndex` | +0x24 | LAYOUT (between proven Handicap +0x0C..+0x18 and type +0x30) | | `Player.h:743` |

Enumerating and classifying:

- Iterate `i < min([PL+0x10], 16)`, `P = [PL+0x14+4i]`, skip NULL and `P == [PL+0x0C]`.
- An AI opponent is `[P+0x30] == 1 && [P+0x150] != 0 && [P+0x448] == 0 && [P+0x262] == 0`.
- The neutral and civilian players are also PLAYER_COMPUTER but are normally created without an AIPlayer. That claim is from the source, not traced, so the `m_ai != 0` test excluding them is listed under `## Unproven`.

Enemy versus ally:

- `Player::getRelationship` answers this through per-player `std::hash_map<PlayerIndex, Relationship>` (`PlayerRelationMap`, `Player.h:152-166`), with ENEMIES 0, NEUTRAL 1, ALLIES 2 (`GameCommon.h:498-502`). A team-level override map is checked first.
- Reading it by value means walking an STLport hashtable in another process. Its internal layout was not mapped (`## Unproven`).
- Until it is, the trainer lets the owner tick which AI players count as "enemy" (allied skirmish AI is a real case) instead of inferring it.

## Safety checks

The cheapest structural test before each write. A failed test skips that object; it never writes a fallback.

| Feature | Checks (all must pass) |
| --- | --- |
| All | The existing guard chain: `[&TheGameLogic]` non-NULL, game mode ∈ {0, 2}, frame advancing. The object comes from `m_objVector` and is non-NULL. Owner via `[[[obj+0x1B0]+0x08]+0x0C]` is the intended player. Read back after every write. Restore per value on disarm (the trainer's existing pattern). |
| Health | `B = [obj+0x174]` non-NULL; `[B]` ∈ the six ActiveBody-family vtables for this build; **`[B−0x08] == obj`** (module back-pointer, PROVEN); `[B+0x10]` finite and > 0; `0 < [B+0x08] ≤ [B+0x10]` (skips dead-but-not-destroyed objects, which have health ≤ 0). |
| Veterancy | `T = [obj+0x18C]` non-NULL; **`[T+0x08] == obj`**; `[T+0x0C]` ∈ 0..3; `[T+0x14] == 0` before the experience write; the body check above passes (it is an ActiveBody-family unit). |
| Ammo | `W = [obj+0x1D8+4i]` non-NULL; `[W+0x10]` ≤ 4; `tmpl = [W+0x08]` non-NULL; `clip = [tmpl+0x164]` in 1..1000 (skip 0, unlimited); `[W+0x14] ≤ clip` or `== 0x7FFFFFFF`. Once confirmed, `[W+0x0C] == i` is a stronger test. |
| Game speed | `GE = [&TheGameEngine]` non-NULL; current `[GE+0x08]` ∈ 1..1000 before the first write (30 in a normal game); the written value is clamped to 1..1000. |
| Other players | `[PL+0x10]` ≤ 16; `P` non-NULL; `[P+0x30]` ∈ {0, 1}; `P` ≠ local; owner-chosen enemy set. |

Edge states:

| State | Handling |
| --- | --- |
| No body | NULL B → skip. Inactive objects carry InactiveBody → the vtable test skips them. |
| No AI | Irrelevant to these writes. |
| No experience tracker | NULL T → skip. |
| Units inside transports or buildings | Still in `m_objVector` and still own their body, so the writes apply. Whether an indestructible passenger survives its container's death is unproven. |
| Structures under construction | Health ramps as they are built. Healing them is harmless (capped at max), but skip them until construction-percent is confirmed (`## Unproven`). |
| Neutral and civilian objects | The owner check excludes them. |
| Allied AI | Excluded unless the owner ticks it. |

## Unproven

| Item | Why | Runtime read that settles it |
| --- | --- | --- |
| Weapon `m_wslot` +0x0C, `m_whenLastReloadStarted` +0x20 | LAYOUT only | `[W+0x0C]` equals the slot index for every weapon of a few units |
| BodyModule `m_currentSubdualDamage` B+0x18 | LAYOUT only (not written by any recipe) | 0.0 on an undamaged unit, rises under a microwave/EMP subdual weapon |
| ExperienceTracker sink +0x14, scalar +0x18 | LAYOUT only | sink 0 and scalar 1.0f on an ordinary tank |
| Player `m_playerIndex` +0x24 | LAYOUT only | equals the player's slot `i` in `m_players` |
| Object→ThingTemplate pointer (for the exact experience threshold at tmpl+0x14C) | `getTemplate` (M 0x543690) not traced; the recipe avoids it by writing 0x0FFFFFFF | trace 0x543690 |
| `m_constructionPercent` at obj+0x19C | LAYOUT between proven +0x190 and +0x1B0, and only if `UpgradeMaskType` is 16 bytes | −1.0f (0xBF800000) on finished objects, 0..100 on a building under construction |
| Relationship maps (STLport `hash_map` inside PlayerRelationMap at +0x04; count at +0x18) | Internal layout not mapped | walk the local player's `[+0x1A4]` map and compare with a known allied/enemy skirmish setup |
| Neutral/civilian players have `m_ai == NULL` | Source reading only | read `[P+0x150]` for every player in a skirmish |
| Jet return-to-base trigger | JetAIUpdate not read | watch a jet's weapon status while it decides to return |
| Health bar reads `getHealth()` at draw | Source reading only (Drawable health bar) | visual check after a heal write |
| Indestructible passengers when their container dies | not traced through OpenContain | in-game test |
| Skirmish runs with `TheNetwork == NULL` | Source reading (`GameEngine.cpp:773`) | speed write in skirmish behaves like campaign |
| Client-side shroud reveal | Writing `W3DShroud` source buffers (`m_finalFogData`, `W3DShroud.cpp:156-157`) and/or raising `GlobalData::m_shroudAlpha` (clamp at `W3DShroud.cpp:295-296`) might lighten the texture without an engine call. Neither the W3DShroud instance path nor the per-frame upload was traced, and logic visibility (hidden enemy units) would be unchanged. | static trace of `W3DShroud::render` and the terrain render object, then a runtime test |
| ThePartitionManager address, PartitionCell size | not mapped (fog verdict does not need them) | |

## Verdict

| Feature | Rating | Reason |
| --- | --- | --- |
| God mode | READY | `m_indestructible` byte at B+0x87 (PROVEN) makes `ActiveBody::attemptDamage` return before any change, including `kill()`. Restore the original byte per object. |
| Instant heal | READY-WITH-CAVEATS | B+0x08 ← B+0x10 is immediate and safe on live objects. Damage-state visuals stay stale until the next health change. |
| Weak enemies | READY-WITH-CAVEATS | `m_damageScalar` B+0x04 (save/restore, overwrites battle-plan modifiers) or health floor ≥ 1.0. Writing ≤ 0 creates an unkillable unit. Highlander/Immortal/Undead resist by design. |
| Veterancy | READY-WITH-CAVEATS | Bonus bits at obj+0x204 give the combat bonus at once. The experience write at T+0x10 gives full, engine-applied promotion on the unit's next experience gain. A level write alone is cosmetic and is undone by the next gain. |
| Ammo | READY-WITH-CAVEATS | Refill `m_ammoInClip` from tmpl+0x164 and reset the 0x7FFFFFFF next-fire frame. Status is recomputed by the engine. A jet that empties between polls may still head home. |
| Fog of war | NOT FEASIBLE AS A VALUE WRITE | The visible shroud and radar update only through engine calls on cell edge-triggers. The only global switch is compiled out of retail builds. |
| Game speed | READY | `[&TheGameEngine]+0x08` (M 0xA29B00 / G 0xA29B80) sets logic frames per second 1:1 in single player. The engine changes it at run time itself. Clamp 1..1000 and never write 0. |
| Broke AI (other players) | READY-WITH-CAVEATS | Enumeration (+0x10/+0x14) and AI detection (+0x30, +0x150) are PROVEN, and money +0x38 is PROVEN. Enemy-versus-ally needs an unmapped hash_map, so the owner picks the targets. |
