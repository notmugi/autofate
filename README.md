# Autofate
![Autofate logo](https://raw.githubusercontent.com/notmugi/autofate/refs/heads/main/Autofate/images/icon.png)  

An FFXIV [Dalamud](https://github.com/goatcorp/Dalamud) plugin that automates FATE farming,
built on puni.sh integrations (ECommons foundation, EzIPC inter-plugin communication).

> [!WARNING]
> **This plugin is VIBE CODED.** It was built almost entirely by prompting an AI, and i do NOT claim to be a programmer. I want this to be abundantly clear so there is no discourse. Use it if you like, or don't if you don't. It works well in practice, but the code is what it is. Use
> at your own risk; Automation is frowned upon and can get you banned. but frankly puni.sh users already know this so idk why i'm even mentioning it.
>
> **Pull requests and help are very welcome.** If you're a real developer and want to clean
> things up, fix the work-in-progress features, or add anything, please open an issue or PR. I'd be happy to take a look and include more human-authored code, as I'd like to get this code off of the slop codebase eventually. 

---

## Features

### Farming modules:
- **Leveling**: farm fates to level your current class to a target level. Automatically teleports to best zone for your current level, with a configurable level based zone cap).
- **Single Zone**: farm one zone (set it to your current zone or pick from a list).
- **Shared FATEs**: rotate through ShB / EW / DT zones, track completion and leave when complete.
- **Atma**: the 12 ARR zodiac relic zones.
- **Demiatma**: the 6 Dawntrail phantom relic zones.
- **Luminous Crystals** & **Memories**: the Heavensward anima relic zones.
- **Manual**: build your own zone list, set fates-per-zone, reset counters, optionally loop it or terminate it when complete.

Collection modes (Atma/Demiatma/Luminous/Memories) track the required items in your inventory, moves on when a zone's items are done, and stops when the whole list is collected.

### Fate engine
- Enable/disable fate types: **Battle, Boss, Defend, Escort** (Collect is WIP, see below).
- Prioritize fates **lower on their timer** instead of closest, with a minimum-time cutoff.
- Run fates up to **N levels above** your level (default 2).
- **Auto level-sync** to the target fate: Sync upon arrival to fate so as not to accidentally sync with fates along the path.
- **Mass-pull** toggle with a configurable enemy cap (can only adhere to this as best as reasonably possible)
- **FATE blacklist**: never navigate to named fates.
- **Follow party leader**: skip our own pathing and just run whatever fate the leader drops us
  in (great for multiboxing & farming with friends).

### Combat
- **Rotation backend:** Wrath Combo or Rotation Solver Reborn.
- **Movement / AOE dodging:** **BossMod Reborn AI is required**. it handles all AOE dodging and general avoidance tech.
- If a required backend isn't installed, Autofate refuses to start and tells you in chat.

### Travel
- vnavmesh navigation with automatic mounting / flight (pick your mount).
- Lifestream for between-zone teleporting and an optional end-of-run command + chocobo leveling (see below)

### Chocobo
- Companion stance (Defender/Attacker/Healer) with **auto-Healer when your or the chocobo's HP drops** below a user-defined threshold.
- Auto re-use **Gysahl Greens** before the companion times out.
- **Auto leveling:** travel home, recall, stable, train, feed Thavnairian Onions and stop at your target rank. You can define the location of your houses chocobo stable and have it automatically use the onion to level your chocobo! **(requires onions to be in inventory)**

### Upkeep
- **Food & potions:** scans the inventory for your food and pots, and it will use them before the timer is up.
- **Auto repair:** self-repair with Dark Matter (stops farming if you run out), with a
  durability threshold.

### Gemstones
- Bicolor gemstone buy-list with per-item targets (or continuous buying) and a buy threshold.
- Auto-travel to your captured vendor, buy, and return to farming.
- Tracks **gross gemstones gained** across the session.

### Stop triggers
- Stop at desired level, gemstone count, chocobo max level, vendor targets met, or (in leveling mode) after dying twice. <- this is to prevent infinitely running overnight and dying over and over like an idiot if you reach a level you do not have gear for.

---

## WIP features

These are present in the code but **disabled/greyed in the UI**. They're good first
contributions — search the code for `TODO(WIP)` to find each one:

- **Mender NPC repair** — only self-repair is automated; NPC routing is unfinished. I didnt have the time, money, or energy to implement a second form of repairs, i work a full time job and just wanted a product that is in a good working state. if you know how to get this working, that would be fantastic. an example of navigating the repair window is currently already available through self repair, and an example of setting a desired npc location is available through the bicolor shop and alternatively through the chocobo stable section.

---
## Installing

**As a repo (recommended):** `/xlsettings` → **Experimental** → **Custom Plugin Repositories**,
paste:

```
https://raw.githubusercontent.com/notmugi/autofate/main/repo.json
```

**As a dev plugin:** add the path to `Autofate.dll` under **Dev Plugin Locations**, then load it
from **Plugin Installer → Dev Tools**.

### Commands
- `/autofates`, `/autofate`, `/af` — open the window.
- `/af start`, `/af stop`, `/af toggle` — control farming.

## Recommended companion plugins
- **vnavmesh** (navigation) — required.
- **BossMod Reborn** — required (movement + AOE dodging).
- **Wrath Combo** or **Rotation Solver Reborn** — at least one, for the rotation.
- **Lifestream** (teleporting / housing travel).
- **AutoRetainer** (optional). (Currently WIP)

---
## Building

Uses **ECommons as a git submodule** (built from source).

```bash
git clone https://github.com/notmugi/autofate
cd autofate
git submodule update --init --recursive
dotnet build Autofate/Autofate.csproj -c Release
```

Output: `Autofate/bin/x64/Release/Autofate.dll`. The build references your local Dalamud dev
libraries at `~/.xlcore/dalamud/Hooks/dev/` (override with `-p:DalamudLibPath=...`). Targets
`net10.0-windows`, Dalamud API level 15.
## Updating the published plugin (maintainer)

```bash
./update-build.sh        # clean rebuild, repackage latest.zip, sync repo.json versions
git commit -am "Update build" && git push
```

The version comes from `Autofate/Autofate.csproj` (`<Version>`). Bump it there before running
the script. See [SETUP.md](SETUP.md) for details and the icon/description instructions.

## Contributing

Issues and PRs are welcome — bug reports, fixes, or finishing the WIP features above. There's no
formal style guide; just keep it readable.
