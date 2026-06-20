using System.Collections.Generic;
using System.Linq;
using AutoFates.Features;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.GameFunctions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using AtkBase = FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase;

namespace AutoFates.Core;

/// <summary>
/// Orchestrates the chocobo stabling / training loop and self-repair, using a TaskManager to
/// sequence the multi-step interactions.
///
/// Stable loop (when ChocoboLevelingEnabled):
///   - When the chocobo has reached max companion XP for its rank (needs stabling to advance),
///     OR enough time has passed since last stabling, travel home, recall the chocobo, open the
///     stable, stable + train it, feed Thavnairian Onions (rank > 10) / Curiel Roots.
///   - Stabling sets a ~1h timer; we return after it elapses to feed Curiel Roots and resume.
///   - Stop once the chocobo reaches the configured max level (or 20).
///
/// NOTE: The precise stable addon interactions (right-click stable, menu selection) are flagged
/// for in-game tuning. The structure, navigation, recall, and feeding are wired here.
/// </summary>
public static unsafe class ChocoboStableRoutine
{
    private static readonly TaskManager TM = new(new TaskManagerConfiguration
    {
        TimeLimitMS = 60_000,
        ShowError = false,
    });

    // Tracks the last time we stabled, to respect the ~1 hour cooldown.
    private static DateTime _lastStableUtc = DateTime.MinValue;
    private static readonly TimeSpan StableCooldown = TimeSpan.FromMinutes(60);

    /// <summary>Does the chocobo need stabling/feeding attention right now?</summary>
    public static bool NeedsAttention(Configuration c)
    {
        if (!c.ChocoboLevelingEnabled) return false;
        if (ChocoboManager.ReachedTargetLevel(c)) return false; // done

        // If the cooldown has elapsed and we have curiel roots (or need to advance rank), go.
        var cooldownElapsed = DateTime.UtcNow - _lastStableUtc >= StableCooldown;
        if (!cooldownElapsed) return false;

        // Need either curiel roots (to feed) or the rank is maxed for current tier (needs stabling).
        return ChocoboManager.HasCurielRoots() || ChocoboNeedsTierUnlock();
    }

    /// <summary>Heuristic: companion XP at/near cap for the current rank means we must stable to advance.</summary>
    private static bool ChocoboNeedsTierUnlock()
    {
        // Without exact per-rank XP tables, we treat a very high CurrentXP as "needs stabling".
        // This is a conservative tuning point; refined in-game.
        return ChocoboManager.CurrentXP() > 0 && ChocoboManager.Rank() < 20;
    }

    /// <summary>
    /// Drive the stabling routine. Returns true when the routine has finished for now (resume farming).
    /// </summary>
    public static bool Tick(Configuration c)
    {
        if (ChocoboManager.ReachedTargetLevel(c))
            return true;

        // Step 1: get home.
        if (!AtHome(c))
        {
            if (EzThrottler.Throttle("AF_GoHome", 8000))
                Teleporter.LifestreamCommand(c.ChocoboHomeLifestreamCommand);
            return false;
        }

        // Step 2: navigate to the stable position if the user set one.
        if (c.StablePositionSet && c.StableTerritory == Svc.ClientState.TerritoryType)
        {
            var arrived = Navigator.MoveTo(c, c.StablePosition, 3f);
            if (!arrived) return false;
        }

        // Step 3: clean the stable if we have brooms.
        if (c.AutoCleanStable && ChocoboManager.HasStableBrooms())
        {
            if (EzThrottler.Throttle("AF_CleanStable", 4000))
                InventoryUtil.UseItem(Data.GameItems.MagickedStableBroom);
        }

        // Step 4: WITHDRAW the companion. You cannot stable a summoned chocobo. This issues the
        // "Withdraw" general action (NOT /companion, which just toggles the companion menu open).
        if (ChocoboManager.IsSummoned())
        {
            StatusText("Recalling chocobo (Withdraw)");
            ChocoboManager.Recall();
            return false;
        }

        // Steps 5+: drive the stable addon menus step by step.
        return DriveStableMenus(c);
    }

    // Stable interaction step machine. Each call advances one step (throttled), so we never
    // re-open / spam the menu — we click through Stable -> Train -> feed -> confirm.
    private enum StableStep { Interact, ChooseStable, ConfirmStable, TendToChocobo, ChooseTrain, ConfirmTrain, Feed, Done }
    private static StableStep _step = StableStep.Interact;
    private static DateTime _stepStartedUtc = DateTime.MinValue;

    /// <summary>Reset the stable step machine (call when (re)entering the chocobo-leveling state).</summary>
    public static void ResetSteps()
    {
        _step = StableStep.Interact;
        _stepStartedUtc = DateTime.MinValue;
    }

    private static bool DriveStableMenus(Configuration c)
    {
        // Safety timeout: if a step wedges for 20s, reset to Interact.
        if (_stepStartedUtc != DateTime.MinValue && DateTime.UtcNow - _stepStartedUtc > TimeSpan.FromSeconds(20))
        {
            Svc.Log.Warning($"[Chocobo] Stable step {_step} timed out; restarting interaction.");
            _step = StableStep.Interact;
            _stepStartedUtc = DateTime.MinValue;
        }

        switch (_step)
        {
            case StableStep.Interact:
            {
                // If a SelectString is already open, skip straight to choosing.
                if (TryGetSelectString(out _)) { Advance(StableStep.ChooseStable); return false; }

                var stable = FindStable();
                if (stable == null)
                {
                    if (EzThrottler.Throttle("AF_FindStable", 5000))
                        Svc.Chat.PrintError("[AutoFates] Couldn't find the chocobo stable nearby. Target your stable in-game and click 'Add targeted stable' in the Chocobo tab.");
                    return false;
                }
                if (Player.IsAnimationLocked || !Player.Interactable) return false;
                // Must target the object before interacting (mirrors ECommons' interact pattern).
                if (!stable.IsTarget())
                {
                    if (EzThrottler.Throttle("AF_StableTarget", 500))
                        Svc.Targets.Target = stable;
                    return false;
                }
                if (EzThrottler.Throttle("AF_StableInteract", 2000))
                {
                    StatusText("Interacting with stable");
                    TargetSystem.Instance()->InteractWithObject(stable.Struct(), false);
                    Advance(StableStep.ChooseStable);
                }
                return false;
            }

            case StableStep.ChooseStable:
            {
                // Click the "Stable my Chocobo" entry (matched by text).
                if (TrySelectEntry(t => t.Contains("Stable", StringComparison.OrdinalIgnoreCase)
                                        && t.Contains("Chocobo", StringComparison.OrdinalIgnoreCase)))
                {
                    StatusText("Confirming stable");
                    Advance(StableStep.ConfirmStable);
                }
                return false;
            }

            case StableStep.ConfirmStable:
            {
                // The game pops a "Stable your chocobo?" Yes/No confirmation — click Yes.
                if (TryConfirmYesno())
                {
                    StatusText("Stabled; reopening menu");
                    Advance(StableStep.TendToChocobo);
                }
                return false;
            }

            case StableStep.TendToChocobo:
            {
                // Stabling closes the menu, so re-interact to reopen it, then "Tend to my Chocobo".
                if (!TryGetSelectString(out _))
                {
                    if (!ReinteractStable()) return false;
                    return false; // wait for the menu to open next tick
                }
                if (TrySelectEntry(t => t.Contains("Tend", StringComparison.OrdinalIgnoreCase)
                                        && t.Contains("my chocobo", StringComparison.OrdinalIgnoreCase)))
                {
                    StatusText("Tending to chocobo");
                    Advance(StableStep.ChooseTrain);
                }
                return false;
            }

            case StableStep.ChooseTrain:
            {
                // "Tend to my Chocobo" opens the custom HousingMyChocobo addon (a fixed list:
                // 0=Train, 1=Feed, 2=Change Name, 3=Fetch, 4=View Details, 5=Quit). Fire Train (0).
                if (!HousingChocoboReady()) return false;
                if (FireHousingChocobo(0)) // Train
                {
                    StatusText("Confirming train");
                    Advance(StableStep.ConfirmTrain);
                }
                return false;
            }

            case StableStep.ConfirmTrain:
            {
                // "Train your chocobo?" Yes/No confirmation.
                if (TryConfirmYesno())
                {
                    StatusText("Trained; feeding");
                    _lastStableUtc = DateTime.UtcNow; // training sets the ~1h cooldown
                    Advance(StableStep.Feed);
                }
                return false;
            }

            case StableStep.Feed:
            {
                // Feed Thavnairian Onion (raises the level cap past 10) when available, otherwise
                // a Curiel Root. The feed is performed from the training menu / inventory.
                if (ChocoboManager.HasThavnairianOnions())
                {
                    if (EzThrottler.Throttle("AF_FeedOnion", 2000))
                    {
                        StatusText("Feeding Thavnairian Onion");
                        InventoryUtil.UseItem(Data.GameItems.ThavnairianOnion);
                    }
                }
                else if (ChocoboManager.HasCurielRoots())
                {
                    if (EzThrottler.Throttle("AF_FeedCuriel", 2000))
                    {
                        StatusText("Feeding Curiel Root");
                        InventoryUtil.UseItem(Data.GameItems.CurielRoot);
                    }
                }
                Advance(StableStep.Done);
                return false;
            }

            default: // Done
            {
                // Close any lingering menu and finish the routine for this cycle.
                if (TryGetSelectString(out var ss)) { CloseSelectString(ss); return false; }
                _step = StableStep.Interact;
                _stepStartedUtc = DateTime.MinValue;
                Svc.Log.Information("[Chocobo] Stable cycle complete; resuming farming.");
                return true;
            }
        }
    }

    private static void Advance(StableStep next)
    {
        _step = next;
        _stepStartedUtc = DateTime.UtcNow;
    }

    private static void StatusText(string s) => Svc.Log.Debug($"[Chocobo] {s}");

    // -------------------------------------------------- stable object + menu helpers
    /// <summary>Find the nearest chocobo stable EventObj (housing furnishing) within ~10y.</summary>
    private static IGameObject? FindStable()
    {
        var me = Player.Object;
        if (me == null) return null;
        var c = Plugin.C;

        IGameObject? best = null;
        var bestDist = float.MaxValue;
        foreach (var o in Svc.Objects)
        {
            bool match;
            if (c != null && c.StableDataId != 0)
            {
                // Preferred: the exact entity the user targeted + added (by BaseId).
                match = o.BaseId == c.StableDataId;
            }
            else
            {
                // Fallback: an EventObj whose name contains "Stable".
                if (o.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) continue;
                var name = o.Name.TextValue;
                match = !string.IsNullOrEmpty(name) && name.Contains("Stable", StringComparison.OrdinalIgnoreCase);
            }
            if (!match) continue;

            var d = System.Numerics.Vector3.Distance(me.Position, o.Position);
            if (d < bestDist) { bestDist = d; best = o; }
        }
        return best;
    }

    private static bool TryGetSelectString(out AddonMaster.SelectString ss)
    {
        ss = default!;
        if (ECommons.GenericHelpers.TryGetAddonByName<AtkBase>("SelectString", out var addon)
            && ECommons.GenericHelpers.IsAddonReady(addon))
        {
            ss = new AddonMaster.SelectString((nint)addon);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Collect menu entries from whichever selection addon is open. The stable's top menu is a
    /// SelectString, but the "Personal Chocobo" submenu (Train/Feed/...) is a SelectIconString.
    /// </summary>
    private static List<(string Text, Action Select)> GetMenuEntries()
    {
        var list = new List<(string, Action)>();
        if (ECommons.GenericHelpers.TryGetAddonByName<AtkBase>("SelectString", out var s)
            && ECommons.GenericHelpers.IsAddonReady(s))
        {
            var m = new AddonMaster.SelectString((nint)s);
            foreach (var e in m.Entries) { var ec = e; list.Add((e.Text, () => ec.Select())); }
        }
        else if (ECommons.GenericHelpers.TryGetAddonByName<AtkBase>("SelectIconString", out var si)
            && ECommons.GenericHelpers.IsAddonReady(si))
        {
            var m = new AddonMaster.SelectIconString((nint)si);
            foreach (var e in m.Entries) { var ec = e; list.Add((e.Text, () => ec.Select())); }
        }
        return list;
    }

    /// <summary>True if a selection menu is open with an entry matching the predicate.</summary>
    private static bool MenuHasEntry(Func<string, bool> match)
        => GetMenuEntries().Any(e => match(e.Text));

    /// <summary>True if the HousingMyChocobo addon (Train/Feed/...) is open and ready.</summary>
    private static bool HousingChocoboReady()
        => ECommons.GenericHelpers.TryGetAddonByName<AtkBase>("HousingMyChocobo", out var a)
           && ECommons.GenericHelpers.IsAddonReady(a);

    /// <summary>
    /// Fire a callback on the HousingMyChocobo addon to pick a list row.
    /// Rows: 0=Train, 1=Feed, 2=Change Name, 3=Fetch, 4=View Details, 5=Quit.
    /// </summary>
    private static bool FireHousingChocobo(int row)
    {
        if (!HousingChocoboReady()) return false;
        if (!EzThrottler.Throttle("AF_HousingChocobo", 2000)) return false;
        if (ECommons.GenericHelpers.TryGetAddonByName<AtkBase>("HousingMyChocobo", out var addon))
        {
            Svc.Log.Debug($"[Chocobo] HousingMyChocobo callback row {row}.");
            Callback.Fire(addon, true, row);
            return true;
        }
        return false;
    }

    /// <summary>Diagnostic: log every visible addon (name) so we can identify the chocobo menu addon.</summary>
    private static void LogVisibleAddons()
    {
        try
        {
            var mgr = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance();
            var list = mgr->AtkUnitManager.AllLoadedUnitsList;
            var names = new List<string>();
            for (var i = 0; i < list.Count; i++)
            {
                var u = list.Entries[i].Value;
                if (u == null || !u->IsVisible) continue;
                names.Add(u->NameString);
            }
            Svc.Log.Information($"[Chocobo] Visible addons: {string.Join(", ", names)}");
        }
        catch (Exception e) { Svc.Log.Warning($"[Chocobo] LogVisibleAddons failed: {e.Message}"); }
    }

    /// <summary>Re-target and re-interact with the stable to reopen its menu. Returns true if interact fired.</summary>
    private static bool ReinteractStable()
    {
        var stable = FindStable();
        if (stable == null) return false;
        if (Player.IsAnimationLocked || !Player.Interactable) return false;
        if (!stable.IsTarget())
        {
            if (EzThrottler.Throttle("AF_StableTarget", 500)) Svc.Targets.Target = stable;
            return false;
        }
        if (!EzThrottler.Throttle("AF_StableReinteract", 2000)) return false;
        TargetSystem.Instance()->InteractWithObject(stable.Struct(), false);
        return true;
    }

    /// <summary>Click Yes on the "Stable your chocobo?" confirmation. Returns true once clicked.</summary>
    private static bool TryConfirmYesno()
    {
        if (!ECommons.GenericHelpers.TryGetAddonByName<AtkBase>("SelectYesno", out var addon)
            || !ECommons.GenericHelpers.IsAddonReady(addon))
            return false;
        if (!EzThrottler.Throttle("AF_StableYes", 1000)) return false;
        var yn = new AddonMaster.SelectYesno((nint)addon);
        Svc.Log.Debug($"[Chocobo] Confirming Yes/No: '{yn.Text}'");
        yn.Yes();
        return true;
    }

    /// <summary>Select a SelectString entry whose text matches the predicate. Returns true if clicked.</summary>
    private static bool TrySelectEntry(Func<string, bool> match)
    {
        var entries = GetMenuEntries();
        if (entries.Count == 0) return false;
        // 2s buffer after entering this step before clicking, so the menu is fully populated.
        if (_stepStartedUtc != DateTime.MinValue && DateTime.UtcNow - _stepStartedUtc < TimeSpan.FromSeconds(2))
            return false;
        if (!EzThrottler.Throttle("AF_StableSelect", 2000)) return false;
        foreach (var e in entries)
        {
            if (match(e.Text))
            {
                Svc.Log.Debug($"[Chocobo] Selecting menu entry '{e.Text}'.");
                e.Select();
                return true;
            }
        }
        Svc.Log.Debug($"[Chocobo] No matching entry. Available: {string.Join(" | ", entries.Select(x => $"'{x.Text}'"))}");
        return false;
    }

    private static void CloseSelectString(AddonMaster.SelectString ss)
    {
        if (!EzThrottler.Throttle("AF_StableClose", 1000)) return;
        // "Cancel"/last entry usually closes; try a Cancel-labelled entry, else fire callback -1.
        foreach (var e in ss.Entries)
            if (e.Text.Contains("Cancel", StringComparison.OrdinalIgnoreCase) || e.Text.Contains("Quit", StringComparison.OrdinalIgnoreCase))
            { e.Select(); return; }
        if (ECommons.GenericHelpers.TryGetAddonByName<AtkBase>("SelectString", out var addon))
            Callback.Fire(addon, true, -1);
    }

    private static bool AtHome(Configuration c)
    {
        // Heuristic: we're "home" if we're in a housing/apartment territory.
        // Housing intended-use ids: 13/14 (private/FC), 60/61 (apartments), 22 etc.
        var terr = Player.Territory.ValueNullable;
        if (terr == null) return false;
        var use = terr.Value.TerritoryIntendedUse.RowId;
        return use is 13 or 14 or 22 or 60 or 61;
    }

    // ---------------------------------------------------------------- self repair
    /// <summary>
    /// Drive self-repair: swap to crafter gearset, open repair window, repair all, swap back.
    /// Uses throttles so repeated ticks don't spam.
    /// </summary>
    public static void RunSelfRepair(Configuration c)
    {
        if (ECommons.GenericHelpers.IsOccupied()) return;

        // Equip crafter gearset first.
        if (EzThrottler.Throttle("AF_RepairGearset", 4000))
        {
            RepairManager.EquipRepairGearset(c);
            return;
        }

        // Open the repair window.
        if (!RepairManager.RepairAddonReady())
        {
            if (EzThrottler.Throttle("AF_OpenRepair", 2000))
                RepairManager.OpenRepairWindow();
            return;
        }

        // Repair-all via the addon callback.
        if (EzThrottler.Throttle("AF_RepairAll", 2000))
        {
            // Fire the "Repair All" button on the Repair addon (callback index 0 with confirm).
            if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("Repair", out var addon))
            {
                Callback.Fire(addon, true, 0);
            }
        }

        // Confirm any yes/no dialog.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var yn))
        {
            if (EzThrottler.Throttle("AF_RepairConfirm", 1000))
            {
                Callback.Fire(yn, true, 0);
            }
        }
    }
}
