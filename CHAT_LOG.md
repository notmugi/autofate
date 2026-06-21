# AutoFates Development Chat Log

This document is a full transcript of the development conversation for the **AutoFates** FFXIV Dalamud plugin.

---

## Initial Request

**User provided reference links:**
- https://dalamud.dev/
- https://dalamud.dev/api/
- https://github.com/awgil/ffxiv_navmesh
- https://github.com/FFXIV-CombatReborn
- https://github.com/awgil/ffxiv_bossmod
- https://github.com/PunishXIV/WrathCombo
- https://github.com/NightmareXIV/Lifestream
- https://github.com/PunishXIV/AutoRetainer
- https://github.com/PunishXIV
- https://github.com/erdelf/AutoDuty

**User:** Develop me an ffxiv plugin using puni.sh paradigms whose main goal is to automate fate farming. The plugin should be able to:

Select the fate farming mode within modes:
- leveling mode
- single zone (button to set current zone)
- shared fates
- atma (ARR zones)
- demiatma (urqopacha, kozama'uka, yak t'el, shaaloani, heritage found, living memory)
- luminous crystals (coerthas western highland, sea of clouds, azys lla, dravanian forelands, churning mists, dravanian hinterlands)
- memories (sea of clouds, dravanian forelands, azys lla)
- manual selection (custom, select zones and how many to do before switching, with reset counter to each zone entry)

Additional features requested:
- stop with selected triggers (level max, bicolor gemstone count, chocobo xp maxed)
- lifestream support to go places when done
- chocobo leveling
- food and potion usage (check timer, use when almost up; scan inventory and populate available combat potions/food)
- follow party leader to a fate instead of autonavigating
- enable/disable mass pulling enemies
- Auto dodge AOEs if not using BMR
- set chocobo stance
- auto re-use Gysahl Greens when chocobo timer is almost up
- go home when chocobo is max xp, set chocobo stable location
- buy list for bicolor gemstones, stop at specified number or continuously buy
- threshold for when to buy items at bicolor gemstone shop
- specify which gemstone shop to buy from
- store/pull items from retainers and chocobo saddlebag (check autoretainer)
- auto repair gear (NPC or self-repair), swap to crafter gearset, disable if no dark matter
- set % at which gear needs repair
- auto clean stable if user has magicked stable brooms
- prioritize fate targets, avoid aggroing non fate targets, keep safe distance
- prioritize fates lower on their timer rather than closer; min time before ignoring
- levels above player to continue running fates, default 2
- smart collection fate turn-in detection (start with 5, do math)
- auto sync to the fate we are going to (avoid syncing to pass-through fates)
- enable/disable fate types: battle, boss, collect, defend, escort

**Atma zones:**
- maiden: Central Shroud
- Scorpion: Southern Thanalan
- waterbearer: Upper La Noscea
- goat: East Shroud
- bull: Eastern Thanalan
- ram: Middle La Noscea
- twins: Western Thanalan
- lion: Outer La Noscea
- fish: Lower La Noscea
- archer: North Shroud
- scales: Central Thanalan
- crab: Western La Noscea

**Demiatma zones:**
- Azurite Demiatma: Urqopacha
- Verdigris Demiatma: Kozama'uka
- Malachite Demiatma: Yak T'el
- Realgar Demiatma: Shaaloani
- Caput Mortuum Demiatma: Heritage Found
- Orpiment Demiatma: Living Memory

**Build location:** current folder, "autofates"

---

## Research & Setup Phase

The assistant performed extensive research:
- Confirmed a `net10.0-windows` Dalamud plugin (API 425/level 15) builds cleanly on the Linux box, referencing local Dalamud dev libs + ECommons.
- Gathered IPC signatures for vnavmesh, WrathCombo, BossMod Reborn, Lifestream, AutoRetainerAPI.
- Found item IDs: Gysahl Greens 4868, Curiel Root 7894, Thavnairian Onion 8166, Magicked Stable Broom 8168.
- De-risked the build toolchain with a build probe.

**Clarifying questions answered by user:**
1. Combat backend: let the user pick which plugin to use; error in chat if none selected/installed.
2. AutoRetainer: use https://github.com/PunishXIV/AutoRetainer
3. Self-repair gearset: have the user set a gearset number.
4. Distribution: just get the plugin working (dev plugin loading).
5. Commands: `/autofate`, `/autofates`, `/af` all work.
6. ECommons: NightmareXIV's library that PunishXIV plugins build on.

**ECommons sourcing:** Decided to use a git submodule from https://github.com/NightmareXIV/ECommons (unpinned, builds from source — the authentic puni.sh approach), rather than a pinned DLL or NuGet.

---

## Build Phase

The assistant built out the full plugin:
- **Enums.cs** — FarmingMode, FateType, ChocoboStance, CombatBackend, RepairMode, FarmState, StopReason
- **Configuration.cs** — full config system (EzConfig)
- **Data/Zones.cs** — Atma, Demiatma, Luminous, Memories zones + SharedFateZones + runtime territory/name resolution
- **Data/GameItems.cs** — item IDs
- **IPC wrappers** — NavmeshIPC, LifestreamIPC, WrathComboIPC, BossModIPC, RotationSolverIPC, AutoRetainerIPC, IPCManager
- **Features** — InventoryUtil, ConsumableManager, RepairManager, ChocoboManager, StorageManager, GemstoneShopper, MountManager
- **Core** — FateSelector, SessionStats, Navigator, Teleporter, FarmingController, ChocoboStableRoutine
- **UI** — MainWindow + 11 tabs (Mode, Fate Engine, Combat, Travel, Chocobo, Consumables, Repair, Gemstone, Storage, Stop Triggers, Status)
- **Plugin.cs** — controller, commands, framework update, EzConfigGui
- **AutoFates.json** manifest (API level 15) + DalamudPackager → latest.zip

Key API facts discovered during build:
- `IClientState.LocalPlayer` → use `ECommons.GameHelpers.Player.Object`
- FateState members: Preparing(3), Running(4), Ending(5), Ended(7), Failed(8)
- IFate.FateId is UInt16, TimeRemaining is Int64
- RowRef<T> uses IsValid + Value/ValueNullable
- IFate.GameData → Fate sheet Rule/EventItem/TurnInEventItem for classification
- FateManager: LevelSync, IsSyncedToFate, SyncedFateId, GetCurrentFateId, GetFateById, CurrentFate
- AgentFateProgress (AgentId 77) = shared FATE tracker
- InventoryItem.Condition + GetConditionPercentage() for repair
- Chocobo stances/Withdraw = BuddyAction sheet (Withdraw=2, Free=4, Defender=5, Attacker=6, Healer=7) via ActionType.BuddyAction
- ECommons.ObjectFunctions.IsHostile (nameplate color) = authoritative hostile detection

The plugin built clean and was committed to git.

---

## Distribution Setup

- Set up GitHub repo at https://github.com/notmugi/autofate
- Initially set up GitHub Actions + releases, then switched (per user) to **branch-served distribution**: `repo.json` + `latest.zip` committed to `main`, served from `raw.githubusercontent.com`.
- No tags, no releases — the way these puni.sh-style plugins are typically distributed.
- Dalamud custom repo URL: `https://raw.githubusercontent.com/notmugi/autofate/main/repo.json`
- `update-build.sh` script to rebuild + copy zip + sync version.

---

## Bug Fixes & Iteration (in-game testing)

### Round 1
1. **Wait for navmesh build** — added `Nav.BuildProgress`/`MeshReady` gate before navigating (was teleporting ahead before mesh built).
2. **BMR detection** — internal name is `BossModReborn` (IPC prefix stays `BossMod.`).
3. **Flying jump spam** — let vnavmesh handle takeoff via `fly=true` instead of manual jump spam; 400ms cooldown fallback.
4. **Dismount + engage on arrival** — dismount first (it blocked the combat backend), then StartCombat.

### Shared FATE tracker → drive logic
- Used `AgentFateProgress` (AgentId 77) to read per-zone CurrentRank/MaxRank/FateProgress/NeededFates/TerritoryTypeId/ZoneName.
- Skip maxed zones; stop trigger when all maxed; live UI table; `EnsureData()` opens the agent to populate.
- Added per-expansion checkboxes (ShB/EW/DT) to select which shared-fate zones to run.

### Combat backend wiring
- Movement/AOE backend dropdown; division between rotation backend (Wrath/RSR) and movement backend (BMR/vnav).
- Eventually simplified: **Movement = BMR just runs `/bmrai on`/`off`**.
- Fixed: BMR not detected, combat not initiating.
- **Combat initiation:** Wrath was setting `InCombatOnly=true` (waited to be hit). Fixed to force `InCombatOnly=false` + `OnlyAttackInCombat=false` so Wrath opens combat. RSR `/rotation auto` already self-engages.

### Mount/dismount loop at fates
- Order-of-operations bug: checked arrival after MoveTo, mounted in the band between radius*0.7 and full radius. Fixed by checking IsInsideFate first, added allowMount flag.

### Smart mix (vnav + BMR AOE avoidance)
- vnav walks to enemy, BMR handles AOE dodging.
- Fixed oscillation (walk out of AOE → walk back in) with a **yield latch + hysteresis** (DangerPresent + 600ms settle window).

### Zone teleport-hop loop
- `EzThrottler.Throttle` returns true on first call → instantly rotated zones. Fixed with a real **dwell timer** (`ZoneDwellSeconds`, default 240s). 0 = never rotate.

### Chocobo leveling (extensive)
- **Withdraw/stances** were searched in wrong sheet (GeneralAction). Fixed: they're **BuddyAction** rows issued via `ActionType.BuddyAction`. Verified against https://exd.camora.dev/sheet/BuddyAction.
- **Stable interaction** rebuilt as a step state machine.
- "Unable to summon companion here" — companion maintenance was re-summoning mid-stable. Fixed by skipping maintenance in ChocoboLeveling state.
- **Targeted-stable capture** — added "Add targeted stable" button storing the entity's DataId/BaseId instead of just position.
- Confirmed "Stable your chocobo?" Yes/No dialog handling.
- Re-open stable menu after stabling to hit "Tend to my Chocobo" → "Train".
- Matched "Tend to **my** Chocobo" specifically (not "a specific chocobo").
- Train click timing: 2s buffer per menu step.
- **HousingMyChocobo addon** — the "Personal Chocobo" menu is a custom addon, not SelectString. Fire its list callback directly (row 0 = Train).
- **Feed**: right-click onion → context menu → "Reward". Used AgentInventoryContext + Request context menu.
- **Fetch** after feeding (row 3) + Yes/No → resume farming.
- Auto-resume mid-loop: detect stage from stable menu contents.
- Removed all **cooldown + Curiel Root** logic — only onions matter (XP food not used).

### AddonDumper dev plugin
- Created a standalone dev plugin (`/addondump`) to dump visible addons + text nodes for automation work. Reusable.

### Navigate to fate center
- Drop player in the center (or closest reachable spot, 4y), not the edge.

### Targeting (extensive)
- Never target non-attackable enemies (friendlies, objective objects like "Fish Basket").
- Initially used BattleNpcSubKind.Combatant — **misclassified** baskets/guards.
- **Fixed: use ECommons.ObjectFunctions.IsHostile (nameplate color)** — the same authoritative method AutoDuty uses.
- Defend fates: protect friendly NPCs being attacked — peel their attacker.
- Made it work for ALL fate types (many fates have guard/escort NPCs not tagged Defend).
- 1-second sticky targeting: keep on enemy, switch off friendly.
- **Clear stray aggro** after a fate: combat-state based; detect enemies targeting me OR my chocobo (via OwnerId); checked continuously.
- While in combat, if target pulled off enemy, re-acquire.

### Sync ASAP
- If standing inside a running fate and not synced, sync immediately (covers starting plugin mid-fate).

### Mass pull + Fate-start NPC
- **Mass pull:** gather ≥5 fate enemies within 2y by walking to nearest ungathered enemy; hold and let backend AoE. Made **caster-safe** (don't yank target mid-cast).
- **Start fates via NPC:** for defend/escort fates needing the orange-"!" NPC — interact, click through Talk dialogue, confirm Yes/No. Reused ECommons AddonMaster (Talk.Click / SelectYesno.Yes).

### Collect fates (real loop)
- Bug: start-NPC logic ran for all types → looped re-opening Item Request on collect fates.
- Fixed: start-NPC logic only for Defend/Escort.
- Real loop: grab ground collectables (EventObj w/ FateId) → turn in to "!" NPC → drive Item Request window (fill slot via context menu → Hand Over via ECommons Request master) → leave at 100% progress.
- Helpers: GetNearestCollectable, GetCollectTurnInNpc.

### Housing no-mount (hard rule)
- `CanMountHere` is hard-false in any housing district (TerritoryIntendedUse 13/14/60), even though the game allows it. No config toggle.

---

## Reference Data Gathered

**BuddyAction sheet** (https://exd.camora.dev/sheet/BuddyAction):
| RowId | Name |
|-------|------|
| 2 | Withdraw |
| 3 | Follow |
| 4 | Free Stance |
| 5 | Defender Stance |
| 6 | Attacker Stance |
| 7 | Healer Stance |

**BuddyRank sheet** (https://exd.camora.dev/sheet/BuddyRank) — ExpRequired per rank:
| Rank | ExpRequired |
|------|-------------|
| 0 | 100 |
| 1 | 4000 |
| 2 | 38000 |
| 3 | 82000 |
| 4 | 158000 |
| 5 | 294000 |
| 6 | 490000 |
| 7 | 700000 |
| 8 | 940000 |
| 9 | 1200000 |
| 10 | 1458000 |
| 11 | 1714000 |
| 12 | 1967000 |
| 13 | 2217000 |
| 14 | 2463000 |
| 15 | 2705000 |
| 16 | 2942000 |
| 17 | 3174000 |
| 18 | 3400000 |
| 19 | 3620000 |
| 20 | 0 |

---

## Outstanding / In-Progress

- **Chocobo leveling XP gate (IN PROGRESS):** Chocobo leveling should only run when the chocobo is at **max XP for its current rank** (needs stabling to advance). The XP gate was removed earlier; needs re-adding using the authoritative BuddyRank ExpRequired values above (compare `CompanionInfo.CurrentXP` against `ExpRequired` for the current `Rank`).

## In-game tuning follow-ups (in README)
- Collect-fate turn-in math, escort pacing, NPC repair nav, chocobo stable menu steps,
  gemstone vendor auto-walk, retainer withdrawal, fate Rule classification verification

## Reference plugins for further debugging
- Questionable https://github.com/PunishXIV/Questionable (great automation patterns)
- Lifestream, AutoRetainer, AutoDuty, WrathCombo, vnavmesh, BMR
