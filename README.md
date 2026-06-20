# AutoFates

An FFXIV Dalamud plugin that automates FATE farming, built with **puni.sh paradigms**
(ECommons foundation, EzIPC inter-plugin communication, TaskManager-driven automation).

PLUGIN IS WORK IN PROGRESS. DO NOT EXPECT IT TO BE USABLE YET.
plugin was made using AI. i do not claim to be a programmer and never have. use at your own risk.
---

## Features

### Farming modes
- **Leveling** – farm fates to level your current class up to a target level.
- **Single Zone** – farm one zone (button to set it to your current zone, or pick from a list).
- **Shared FATEs** – rotates through all Shadowbringers / Endwalker / Dawntrail overworld zones.
- **Atma** – the 12 ARR Zodiac zones.
- **Demiatma** – the 6 Dawntrail zones (Urqopacha, Kozama'uka, Yak T'el, Shaaloani, Heritage Found, Living Memory).
- **Luminous Crystals** – the 6 Heavensward zones.
- **Memories** – the 3 Heavensward relic zones.
- **Manual** – build your own zone list, set how many fates per zone, reset counters, and optionally loop.

### Fate engine
- Enable/disable fate types: **Battle, Boss, Collect, Defend, Escort**.
- Prioritize fates **lower on their timer** instead of closest, with a configurable minimum time before a fate is ignored.
- Run fates up to **N levels above** your level (default 2).
- **Auto level-sync** to the target fate — only syncs once you arrive, so you don't sync to fates you pass through.
- Mass-pull toggle, safe-distance from non-fate enemies, auto AOE dodge (when not using BMR).

### Combat backends (you choose)
- **Rotation:** Wrath Combo, Rotation Solver Reborn, or BossMod Reborn autorotation.
- **Movement / AOE dodging:** BossMod Reborn AI, or vnavmesh-only.
- If a selected backend isn't installed, AutoFates refuses to start and tells you in chat.

### Travel
- vnavmesh navigation with **automatic mounting and flight** (pick your preferred mount).
- Lifestream for between-zone teleporting and an optional "go here when finished" command.

### Chocobo
- Companion stance (Defender/Attacker/Healer) with **auto-Healer-stance when your HP drops** below a threshold.
- Auto re-use **Gysahl Greens** before the companion times out.
- **Auto chocobo leveling:** travel home, recall, stable, train, feed Thavnairian Onions / Curiel Roots, auto-clean with Magicked Stable Brooms, and stop at your target rank.

### Upkeep
- **Food & potions:** scans your inventory, lets you pick what you own, and re-applies before the buff expires.
- **Auto repair:** self-repair (swaps to your crafter gearset, needs Dark Matter — stops farming if you run out) or mender NPC, with a durability threshold.
- **Storage:** pull consumables from the chocobo saddlebag (retainer support via AutoRetainer is a work-in-progress).

### Gemstones
- Bicolor gemstone buy-list with per-item target quantities (or continuous buying), a buy threshold, and live import from an open vendor.
- Tracks **gross gemstones gained** across the whole session (auto-purchases are *not* subtracted from the count).

### Stop triggers
- Stop at desired level, gemstone count, chocobo max level, or when all vendor buy-list targets are met.

---

## Building

This repo uses **ECommons as a git submodule** (built from source, not version-pinned).

```bash
git clone <this repo>
cd Autofates
git submodule update --init --recursive
dotnet build AutoFates/AutoFates.csproj -c Release
```

Output: `AutoFates/bin/x64/Release/AutoFates.dll` (and a packaged `AutoFates/latest.zip`).

The build references your local Dalamud dev libraries at `~/.xlcore/dalamud/Hooks/dev/`
(override with `-p:DalamudLibPath=...`). Targets `net10.0-windows`, Dalamud API level 15.

## Installing (dev plugin)

1. In-game: **Dalamud Settings → Experimental → Dev Plugin Locations**.
2. Add the full path to `AutoFates.dll` above.
3. **Plugin Installer → Dev Tools → Installed Dev Plugins → AutoFates → Load**.

### Commands
- `/autofates`, `/autofate`, `/af` — open the window.
- `/af start`, `/af stop`, `/af toggle` — control farming.

## Recommended companion plugins
- **vnavmesh** (navigation) — required unless you only use follow-party-leader with BMR.
- **Lifestream** (teleporting / housing travel).
- **BossMod Reborn**, **Wrath Combo**, or **Rotation Solver Reborn** (at least one, for combat).
- **AutoRetainer** (optional, for retainer storage).

---

## Known tuning points (work-in-progress)

These are wired up structurally but were written without a live game to verify against, so
expect to refine them during testing:

- **Collect-fate hand-in math** — item pickup + turn-in addon flow is stubbed; the
  classification and target-item detection work, but the precise "turn in N, measure delta,
  collect more" loop needs in-game calibration.
- **Escort fates** — follow/defend behavior relies on the combat backend; manual escort pacing
  is minimal.
- **NPC repair navigation** — only self-repair is automated for now.
- **Chocobo stable addon interactions** — navigation, recall, and feeding are wired; the exact
  right-click-stable menu steps need verification.
- **Gemstone vendor navigation** — purchasing from an open vendor works (via ECommons
  AddonMaster); auto-walking to the vendor NPC is not yet automated (open it yourself).
- **Retainer withdrawal** — detection is in place; item movement is deferred (Artisan /
  GatherBuddy Reborn / Vulcan are good references for full retainer inventory access).

Fate-type icon ids and `Fate.Rule` values used for classification are best-effort and may need
adjustment once observed in-game.
