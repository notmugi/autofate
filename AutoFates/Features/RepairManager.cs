using ECommons.Automation;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoFates.Features;

/// <summary>
/// Gear durability monitoring and repair.
///  - Self-repair: swaps to the configured crafter gearset, opens the Repair window, repairs all.
///    Requires dark matter; if none is available the controller will stop farming.
///  - NPC repair: handled by navigating to a mender NPC (driven by the controller).
/// </summary>
public static unsafe class RepairManager
{
    /// <summary>Lowest durability percent across all equipped gear (0-100).</summary>
    public static int GetLowestDurabilityPercent()
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 100;
            var container = im->GetInventoryContainer(InventoryType.EquippedItems);
            if (container == null || !container->IsLoaded) return 100;

            var lowest = 100;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;
                // GetConditionPercentage returns 0..100. Items without durability report 100/0 safely.
                var pct = slot->GetConditionPercentage();
                // Skip items that have no durability tracking (soul crystals report 0 both ways).
                if (slot->Condition == 0 && pct == 0) continue;
                if (pct < lowest) lowest = pct;
            }
            return lowest;
        }
        catch { return 100; }
    }

    public static bool NeedsRepair(Configuration c) => GetLowestDurabilityPercent() <= c.RepairThresholdPercent;

    /// <summary>Can we self-repair right now? (have dark matter)</summary>
    public static bool CanSelfRepair() => InventoryUtil.HasAnyDarkMatter();

    /// <summary>Open the self-repair window (AgentRepair). Returns whether the call ran.</summary>
    public static bool OpenRepairWindow()
    {
        try
        {
            // Open the self-repair window via General Action 6 ("Repair") — this is how GatherBuddy
            // Reborn does it. AgentRepair.Show() does NOT reliably open the window.
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6);
            return true;
        }
        catch (Exception e)
        {
            Svc.Log.Verbose($"[Repair] OpenRepairWindow failed: {e.Message}");
            return false;
        }
    }

    public static bool IsRepairWindowOpen()
        => Svc.GameGui.GetAddonByName("Repair", 1) != nint.Zero;

    /// <summary>Close the Repair window (equivalent to pressing Escape) once repairs are done.</summary>
    /// <summary>
    /// Close the Repair window. Mirrors GatherBuddy Reborn's TaskCloseRepairWindow: fire the
    /// addon's cancel callback (-1). Returns true once the window is gone (caller retries until
    /// then). Tracks attempts and falls back to hiding the agent directly if Close keeps failing.
    /// </summary>
    private static int _repairCloseAttempts;
    public static bool CloseRepairWindow()
    {
        if (!ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>("Repair", out var addon)
            || addon == null || !addon->IsVisible)
        {
            _repairCloseAttempts = 0;
            return true; // already closed
        }

        _repairCloseAttempts++;
        if (_repairCloseAttempts > 20)
        {
            // Fallback: hide the Repair agent directly (GBR does this on timeout).
            try { AgentModule.Instance()->GetAgentByInternalId(AgentId.Repair)->Hide(); }
            catch (Exception e) { Svc.Log.Verbose($"[Repair] Agent Hide fallback failed: {e.Message}"); }
            return false;
        }

        try { Callback.Fire(addon, true, -1); }
        catch (Exception e) { Svc.Log.Verbose($"[Repair] CloseRepairWindow failed: {e.Message}"); }
        return false; // not confirmed closed yet
    }

    /// <summary>Returns true when the Repair addon is open and ready.</summary>
    public static bool RepairAddonReady()
        => ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>("Repair", out var addon)
           && ECommons.GenericHelpers.IsAddonReady(addon);

    /// <summary>
    /// Drive self-repair to completion. No crafter gearset is needed any more — you can repair any
    /// gear with the appropriate grade of dark matter regardless of class. Flow:
    ///   1. Open the Repair window (AgentRepair).
    ///   2. Click "Repair All" (repairs inventory + Armoury Chest + equipped).
    ///   3. Confirm the "Repair as many items as possible...?" Yes/No.
    /// Each step is throttled and gated on IsAddonReady so we never NRE on a half-built addon.
    /// </summary>
    /// <summary>
    /// Click the top-left "Repair All Inventories" button on the Repair addon. This is a separate
    /// control from the right-side "Repair All" (equipped-only) button that AddonMaster.RepairAll
    /// fires. We locate it by walking the addon's component buttons and matching the button whose
    /// label text is "Repair All Inventories"; if found we fire its click event.
    /// </summary>
    /// <summary>Drive self-repair. Returns true once everything is repaired and the window is closed.</summary>
    public static bool RunSelfRepair(Configuration c)
    {
        // Must be grounded: General Action 6 (Repair) can't be used while mounted/flying.
        if (MountManager.IsMounted || MountManager.IsFlying)
        {
            MountManager.Dismount();
            return false;
        }

        // IsOccupied() also returns true if the player isn't targetable; don't block on that here —
        // only block on the genuine "busy" states by checking it AFTER the addon-open paths so an
        // already-open Repair/SelectYesno window still gets driven.
        if (!RepairAddonReady() && ECommons.GenericHelpers.IsOccupied())
        {
            if (EzThrottler.Throttle("AF_RepairOccupied", 5000))
                Svc.Log.Debug($"[Repair] Waiting: occupied. Mounted={MountManager.IsMounted} DM={CanSelfRepair()} LowestDur={GetLowestDurabilityPercent()}%");
            return false;
        }

        // ALL DONE: nothing left damaged. Close the Repair window, retrying until it's actually
        // gone (CloseRepairWindow returns true only once the addon is no longer visible).
        if (GetLowestDurabilityPercent() >= 100)
        {
            if (EzThrottler.Throttle("AF_RepairClose", 300))
                return CloseRepairWindow();
            return false;
        }

        // 1) Open the Repair window if it isn't up yet.
        if (!RepairAddonReady())
        {
            if (EzThrottler.Throttle("AF_OpenRepair", 2000))
            {
                Svc.Log.Debug("[Repair] Opening repair window via General Action 6.");
                OpenRepairWindow();
            }
            return false;
        }

        // 2) Confirm the Yes/No dialog if it's up (this appears after we click Repair All).
        if (ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yn)
            && ECommons.GenericHelpers.IsAddonReady(yn))
        {
            if (EzThrottler.Throttle("AF_RepairConfirm", 800))
            {
                try { new AddonMaster.SelectYesno((nint)yn).Yes(); }
                catch (Exception e) { Svc.Log.Verbose($"[Repair] Confirm Yes failed (mid-transition): {e.Message}"); }
            }
            return false;
        }

        // 3) Click "Repair All" via ECommons.
        if (EzThrottler.Throttle("AF_RepairAll", 1500))
        {
            if (ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>("Repair", out var rep))
            {
                try { new AddonMaster.Repair((nint)rep).RepairAll(); }
                catch (Exception e) { Svc.Log.Verbose($"[Repair] RepairAll failed: {e.Message}"); }
            }
        }
        return false;
    }
}
