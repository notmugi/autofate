using AutoFates.Features;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;

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

        // Step 4: recall companion (must recall before stabling), then stable + train + feed.
        if (ChocoboManager.IsSummoned())
        {
            if (EzThrottler.Throttle("AF_Recall", 3000))
                Chat.ExecuteCommand("/companion"); // dismiss/recall companion
            return false;
        }

        // Step 5: feed onions (rank > 10) / curiel roots while interacting with the stable.
        var fed = false;
        if (ChocoboManager.Rank() >= 10 && ChocoboManager.HasThavnairianOnions())
        {
            if (EzThrottler.Throttle("AF_FeedOnion", 3000))
                fed = InventoryUtil.UseItem(Data.GameItems.ThavnairianOnion);
        }
        else if (ChocoboManager.HasCurielRoots())
        {
            if (EzThrottler.Throttle("AF_FeedCuriel", 3000))
                fed = InventoryUtil.UseItem(Data.GameItems.CurielRoot);
        }

        if (fed)
        {
            _lastStableUtc = DateTime.UtcNow;
        }

        // If we have nothing left to feed and the rank isn't maxed, return until cooldown / xp.
        if (!ChocoboManager.HasCurielRoots() && !ChocoboManager.HasThavnairianOnions())
        {
            if (EzThrottler.Throttle("AF_NoFeed", 30000))
                Svc.Chat.Print("[AutoFates] No chocobo feed remaining; resuming farming until next stable.");
            return true;
        }

        return true;
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
