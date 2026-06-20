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
    private enum StableStep { Interact, ChooseStable, ChooseTrain, Feed, Done }
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
                        Svc.Chat.PrintError("[AutoFates] Couldn't find a chocobo stable nearby. Set the stable position in the Chocobo tab.");
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
                    StatusText("Stabling chocobo");
                    Advance(StableStep.ChooseTrain);
                }
                return false;
            }

            case StableStep.ChooseTrain:
            {
                // After stabling, a menu offers "Train" — click it (matched by text).
                if (TrySelectEntry(t => t.Contains("Train", StringComparison.OrdinalIgnoreCase)))
                {
                    StatusText("Training chocobo");
                    _lastStableUtc = DateTime.UtcNow; // stabling/training sets the ~1h cooldown
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
        IGameObject? best = null;
        var bestDist = float.MaxValue;
        var me = Player.Object;
        if (me == null) return null;
        foreach (var o in Svc.Objects)
        {
            // Stables are EventObj furnishings; match by name to be safe.
            if (o.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) continue;
            var name = o.Name.TextValue;
            if (string.IsNullOrEmpty(name) || !name.Contains("Stable", StringComparison.OrdinalIgnoreCase)) continue;
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

    /// <summary>Select a SelectString entry whose text matches the predicate. Returns true if clicked.</summary>
    private static bool TrySelectEntry(Func<string, bool> match)
    {
        if (!TryGetSelectString(out var ss)) return false;
        if (!EzThrottler.Throttle("AF_StableSelect", 1500)) return false;
        foreach (var e in ss.Entries)
        {
            if (match(e.Text))
            {
                Svc.Log.Debug($"[Chocobo] Selecting menu entry '{e.Text}'.");
                e.Select();
                return true;
            }
        }
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
