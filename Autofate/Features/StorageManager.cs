using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Autofate.Features;

/// <summary>
/// Item storage helpers for the chocobo saddlebag and retainers.
///
/// Notes / limitations (flagged for in-game tuning):
///  - The chocobo saddlebag is always readable/writable while not in combat; we can move items
///    to/from it directly via InventoryManager.MoveItemSlot once the saddlebag is "open" (the
///    game requires the saddlebag UI to have been opened at least once this session).
///  - Retainer item access requires the retainer bell interaction and is best delegated to
///    AutoRetainer. We detect AutoRetainer (see <see cref="Autofate.IPC.AutoRetainerIPC"/>) and
///    avoid running while it is busy. Full automated retainer withdrawal is a follow-up item.
/// </summary>
public static unsafe class StorageManager
{
    public static readonly InventoryType[] SaddlebagTypes =
    {
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    };

    /// <summary>Total count of an item across the chocobo saddlebag (both pages + premium).</summary>
    public static int GetSaddlebagCount(uint itemId)
    {
        var total = 0;
        foreach (var t in SaddlebagTypes)
            total += InventoryUtil.GetItemCountInContainer(itemId, t);
        return total;
    }

    /// <summary>Whether the saddlebag containers are currently loaded (UI was opened this session).</summary>
    public static bool SaddlebagAvailable()
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return false;
            var c = im->GetInventoryContainer(InventoryType.SaddleBag1);
            return c != null && c->IsLoaded;
        }
        catch { return false; }
    }

    /// <summary>
    /// Move a quantity of an item from the saddlebag into the main inventory. Returns the amount
    /// actually moved. Requires the saddlebag to be available (opened this session).
    /// </summary>
    public static int WithdrawFromSaddlebag(uint itemId, int quantity)
    {
        if (quantity <= 0 || !SaddlebagAvailable()) return 0;
        var moved = 0;
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 0;
            foreach (var st in SaddlebagTypes)
            {
                var container = im->GetInventoryContainer(st);
                if (container == null || !container->IsLoaded) continue;
                for (var i = 0; i < container->Size && moved < quantity; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != itemId) continue;
                    var take = (int)slot->Quantity;

                    // Find an empty destination slot in the main inventory.
                    if (!TryFindEmptyMainSlot(im, out var dstType, out var dstSlot)) break;

                    // Move the whole stack; the game merges into existing stacks automatically.
                    var result = im->MoveItemSlot(st, (ushort)i, dstType, dstSlot, true);
                    if (result == 0) moved += take;
                }
            }
        }
        catch (Exception e)
        {
            Svc.Log.Verbose($"[Storage] WithdrawFromSaddlebag failed: {e.Message}");
        }
        return moved;
    }

    private static readonly InventoryType[] MainInventory =
    {
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    };

    private static bool TryFindEmptyMainSlot(InventoryManager* im, out InventoryType type, out ushort slot)
    {
        type = InventoryType.Inventory1;
        slot = 0;
        foreach (var t in MainInventory)
        {
            var container = im->GetInventoryContainer(t);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var s = container->GetInventorySlot(i);
                if (s == null || s->ItemId == 0)
                {
                    type = t;
                    slot = (ushort)i;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Ensure we have at least <paramref name="desired"/> of an item in the main inventory,
    /// pulling from the saddlebag if needed. Returns the resulting inventory count.
    /// </summary>
    public static int EnsureFromStorage(Configuration c, uint itemId, int desired)
    {
        var have = InventoryUtil.GetItemCount(itemId);
        if (have >= desired) return have;

        if (c.UseSaddlebag && SaddlebagAvailable())
        {
            var need = desired - have;
            WithdrawFromSaddlebag(itemId, need);
            have = InventoryUtil.GetItemCount(itemId);
        }

        // Retainer pull is delegated to AutoRetainer / manual for now.
        return have;
    }
}
