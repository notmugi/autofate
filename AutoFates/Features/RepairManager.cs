using ECommons.Automation;
using ECommons.DalamudServices;
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

    /// <summary>Switch to the configured crafter gearset (1-based number) for self repair.</summary>
    public static void EquipRepairGearset(Configuration c)
    {
        if (c.RepairGearsetNumber <= 0) return;
        Chat.ExecuteCommand($"/gearset change {c.RepairGearsetNumber}");
    }

    /// <summary>Open the self-repair window (AgentRepair). Returns whether the call ran.</summary>
    public static bool OpenRepairWindow()
    {
        try
        {
            AgentRepair.Instance()->AgentInterface.Show();
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

    /// <summary>Returns true when the Repair addon is open and ready.</summary>
    public static bool RepairAddonReady()
        => ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>("Repair", out var addon)
           && addon->IsVisible;
}
