using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;

namespace Autofate.Features;

/// <summary>
/// Refreshes food/potion buffs by re-using the configured item before the Well Fed (48) /
/// Medicated (49) status expires.
/// </summary>
public static class ConsumableManager
{
    private const uint WellFedStatus = 48;
    private const uint MedicatedStatus = 49;

    /// <summary>Called every frame from the controller while farming. Returns true if it issued a use.</summary>
    public static bool Tick(Configuration c)
    {
        if (Player.Object == null) return false;
        if (ECommons.GenericHelpers.IsOccupied()) return false; // not while in a menu/cutscene

        var used = false;
        if (c.UseFood && c.FoodItemId != 0)
            used |= MaybeUse(c.FoodItemId, c.FoodIsHq, WellFedStatus, c.ConsumableReuseSeconds, "food");

        if (c.UsePotion && c.PotionItemId != 0)
            used |= MaybeUse(c.PotionItemId, c.PotionIsHq, MedicatedStatus, c.ConsumableReuseSeconds, "potion");

        return used;
    }

    private static bool MaybeUse(uint itemId, bool hq, uint statusId, int reuseSeconds, string label)
    {
        var remaining = InventoryUtil.GetStatusRemaining(statusId);
        if (remaining > reuseSeconds) return false; // still buffed

        if (InventoryUtil.GetItemCount(itemId, hq) <= 0)
        {
            if (EzThrottler.Throttle($"AF_NoConsumable_{itemId}", 30_000))
                Svc.Log.Warning($"[Consumable] Configured {label} (item {itemId}) not in inventory.");
            return false;
        }

        // Throttle to avoid spamming use during the buff's animation lock.
        if (!EzThrottler.Throttle($"AF_UseConsumable_{itemId}", 5_000)) return false;

        Svc.Log.Debug($"[Consumable] Re-applying {label} (item {itemId}, {remaining:0}s remaining).");
        return InventoryUtil.UseItem(itemId, hq);
    }
}
