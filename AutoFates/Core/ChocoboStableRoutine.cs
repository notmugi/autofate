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

        // Stabling REQUIRES Thavnairian Onions (they raise the rank cap). No onions -> skip the
        // whole chocobo-leveling loop entirely; don't interrupt farming.
        var onions = ChocoboManager.HasThavnairianOnions();
        var onionCount = InventoryUtil.GetItemCount(Data.GameItems.ThavnairianOnion);
        var cooldownLeft = StableCooldown - (DateTime.UtcNow - _lastStableUtc);
        var cooldownElapsed = cooldownLeft <= TimeSpan.Zero;

        if (EzThrottler.Throttle("AF_ChocoDiag", 10_000))
            Svc.Log.Information($"[Chocobo] NeedsAttention: onions={onionCount} rank={ChocoboManager.Rank()} target={c.ChocoboTargetLevel} cooldownLeft={(cooldownElapsed ? 0 : cooldownLeft.TotalMinutes):0.0}m");

        if (!onions)
        {
            if (EzThrottler.Throttle("AF_NoOnions", 60_000))
                Svc.Log.Information("[Chocobo] No Thavnairian Onions; skipping chocobo leveling.");
            return false;
        }

        // Respect the ~1h stable cooldown (can't stable again until it elapses).
        if (!cooldownElapsed) return false;

        // We have onions, we're under target level, and the cooldown is up -> stable now.
        // (Chocobo leveling is top priority; we don't gate on a fragile CurrentXP heuristic.)
        return true;
    }

    /// <summary>Heuristic: companion XP at/near cap for the current rank means we must stable to advance.</summary>


    /// <summary>
    /// Drive the stabling routine. Returns true when the routine has finished for now (resume farming).
    /// </summary>
    public static bool Tick(Configuration c)
    {
        if (ChocoboManager.ReachedTargetLevel(c))
            return true;

        // If the stable entity is already loaded nearby, we're home — NEVER teleport. Go straight
        // to stabling. This is the bulletproof check (no territory/intended-use guessing).
        var stableObj = FindStable();
        var stableNearby = stableObj != null;

        if (!stableNearby)
        {
            // Step 1: get home (only if we genuinely can't see the stable).
            if (!AtHome(c))
            {
                if (EzThrottler.Throttle("AF_GoHome", 8000))
                    Teleporter.LifestreamCommand(c.ChocoboHomeLifestreamCommand);
                return false;
            }

            // Step 2: navigate toward the captured stable position to make the stable load in.
            if (c.StablePositionSet && c.StableTerritory == Svc.ClientState.TerritoryType)
            {
                var arrived = Navigator.MoveTo(c, c.StablePosition, 3f);
                if (!arrived) return false;
            }
            return false; // re-check for the stable entity next tick
        }

        // Step 2b: walk into interaction range of the stable if we're too far.
        if (stableObj != null && Player.Object != null
            && System.Numerics.Vector3.Distance(Player.Object.Position, stableObj.Position) > 4f)
        {
            Navigator.MoveTo(c, stableObj.Position, 3f);
            return false;
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
    private enum StableStep { Interact, ChooseStable, ConfirmStable, TendToChocobo, ChooseTrain, ConfirmTrain, Feed, FetchTend, FetchSelect, Done }
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
                // Branch on the menu contents to auto-resume from the correct stage:
                //  - "Stable my Chocobo" present  => chocobo is OUT => stable it, then train/feed/fetch.
                //  - "Tend to my Chocobo" present => chocobo is ALREADY stabled. We were interrupted
                //    before fetching, so just Tend -> Fetch it back out (don't re-train; likely on
                //    cooldown). Leveling then re-evaluates with the chocobo out.
                var entries = GetMenuEntries();
                if (entries.Count == 0) return false; // wait for the menu to populate

                var hasStable = entries.Any(e => e.Text.Contains("Stable", StringComparison.OrdinalIgnoreCase)
                                                 && e.Text.Contains("Chocobo", StringComparison.OrdinalIgnoreCase));
                var hasTend = entries.Any(e => e.Text.Contains("Tend", StringComparison.OrdinalIgnoreCase)
                                               && e.Text.Contains("my chocobo", StringComparison.OrdinalIgnoreCase));

                if (!hasStable && hasTend)
                {
                    // Already stabled but not fetched -> resume at fetch.
                    Svc.Log.Information("[Chocobo] Chocobo already stabled; resuming at Fetch.");
                    Advance(StableStep.FetchTend);
                    return false;
                }

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
                // Training opens a "Reward" prompt = the Inventory addon in selection mode. You
                // reward (feed) by right-clicking the item and choosing "Reward". We automate that:
                // open the item's context menu against the Inventory addon, then click "Reward".
                // Reward only Thavnairian Onions (raise the rank cap). Curiel Roots are an XP buff,
                // NOT a stable reward, so never reward them here.
                var feedItem = ChocoboManager.HasThavnairianOnions() ? Data.GameItems.ThavnairianOnion : 0u;
                if (feedItem == 0)
                {
                    // No onions to reward; just finish.
                    Advance(StableStep.Done);
                    return false;
                }

                // 1) If the Reward context menu is open, click "Reward". Detect by existence+visible
                // (NOT IsAddonReady — that returns false during the open animation, which made step 2
                // re-fire OpenForItemSlot and toggle the menu shut => the flashing loop).
                if (ECommons.GenericHelpers.TryGetAddonByName<AtkBase>("ContextMenu", out var ctx)
                    && ctx != null && ctx->IsVisible)
                {
                    if (!EzThrottler.Throttle("AF_RewardClick", 500)) return false;
                    var cm = new AddonMaster.ContextMenu((nint)ctx);
                    var entries = cm.Entries;
                    if (entries.Length == 0) return false; // not populated yet; wait
                    foreach (var e in entries)
                    {
                        if (e.Text.Contains("Reward", StringComparison.OrdinalIgnoreCase))
                        {
                            Svc.Log.Information($"[Chocobo] Context entry '{e.Text}' -> reward {feedItem}.");
                            e.Select();
                            Advance(StableStep.FetchTend);
                            return false;
                        }
                    }
                    Svc.Log.Information($"[Chocobo] Reward not in context menu. Available: {string.Join(" | ", entries.Select(x => $"'{x.Text}'"))}");
                    return false;
                }

                // 2) Otherwise open the feed item's right-click context menu. Use a longer throttle
                // so we don't re-fire while the menu is opening.
                if (!EzThrottler.Throttle("AF_FeedOpen", 3000)) return false;
                var addonId = GetAddonId("InventoryExpansion");
                if (addonId == 0) addonId = GetAddonId("Inventory");
                if (addonId == 0) addonId = GetAddonId("InventoryLarge");
                var slot = InventoryUtil.FindItemSlot(feedItem);
                StatusText($"Opening reward context for {feedItem}");
                Svc.Log.Information($"[Chocobo] Opening context menu for item {feedItem} (slot {slot?.Slot}, container {slot?.Type}, addonId {addonId}).");
                InventoryUtil.OpenItemContextMenu(feedItem, addonId);
                return false;
            }

            case StableStep.FetchTend:
            {
                // Feeding closed the menu. Re-interact and pick "Tend to my Chocobo" again to get
                // back to the HousingMyChocobo menu so we can Fetch (withdraw from the stable).
                if (!TryGetSelectString(out _))
                {
                    if (!ReinteractStable()) return false;
                    return false;
                }
                if (TrySelectEntry(t => t.Contains("Tend", StringComparison.OrdinalIgnoreCase)
                                        && t.Contains("my chocobo", StringComparison.OrdinalIgnoreCase)))
                {
                    StatusText("Fetching chocobo");
                    Advance(StableStep.FetchSelect);
                }
                return false;
            }

            case StableStep.FetchSelect:
            {
                // HousingMyChocobo rows: 0=Train, 1=Feed, 2=Change Name, 3=Fetch, 4=View Details, 5=Quit.
                if (!HousingChocoboReady()) return false;
                if (FireHousingChocobo(3)) // Fetch -> pulls the chocobo out so we can resume farming
                {
                    StatusText("Fetched; resuming farming");
                    Advance(StableStep.Done);
                }
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

    /// <summary>Get the addon id of a visible addon by name (0 if not open).</summary>
    private static uint GetAddonId(string name)
    {
        if (ECommons.GenericHelpers.TryGetAddonByName<AtkBase>(name, out var a) && a != null)
            return a->Id;
        return 0;
    }

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
        // Most reliable: we're "home" if we're already in the territory where the user captured
        // their stable. This avoids guessing housing TerritoryIntendedUse ids (which caused the
        // teleport loop when the real id wasn't in our list).
        if (c.StablePositionSet && c.StableTerritory != 0)
            return Svc.ClientState.TerritoryType == c.StableTerritory;

        // Fallback (no stable captured yet): treat housing/apartment territories as home.
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
