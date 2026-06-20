using System.Linq;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace AutoFates.Features;

/// <summary>Helpers for inventory counts, item usage, and consumable/status detection.</summary>
public static unsafe class InventoryUtil
{
    /// <summary>Total count of an item across the player's inventories (optionally HQ only).</summary>
    public static int GetItemCount(uint itemId, bool hqOnly = false)
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 0;
            return im->GetInventoryItemCount(itemId, hqOnly, checkEquipped: false, checkArmory: false);
        }
        catch { return 0; }
    }

    /// <summary>Count an item in a specific container.</summary>
    public static int GetItemCountInContainer(uint itemId, InventoryType type, bool hqOnly = false)
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return 0;
            var container = im->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) return 0;
            var total = 0;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId != itemId) continue;
                if (hqOnly && !slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)) continue;
                total += (int)slot->Quantity;
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>Use an inventory item (food, potion, gysahl greens, etc). Returns whether the call ran.</summary>
    public static bool UseItem(uint itemId, bool hq = false)
    {
        try
        {
            var aic = AgentInventoryContext.Instance();
            if (aic == null) return false;
            // HQ items are addressed as itemId + 1_000_000.
            var id = hq ? itemId + 1_000_000u : itemId;
            aic->UseItem(id);
            return true;
        }
        catch (Exception e)
        {
            Svc.Log.Verbose($"[Inventory] UseItem {itemId} failed: {e.Message}");
            return false;
        }
    }

    private static readonly InventoryType[] PlayerBags =
        { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };

    /// <summary>Find the (container, slot) holding an item, or null if not present.</summary>
    public static (InventoryType Type, int Slot)? FindItemSlot(uint itemId)
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return null;
            foreach (var type in PlayerBags)
            {
                var container = im->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded) continue;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot != null && slot->ItemId == itemId)
                        return (type, i);
                }
            }
        }
        catch (Exception e) { Svc.Log.Verbose($"[Inventory] FindItemSlot {itemId} failed: {e.Message}"); }
        return null;
    }

    /// <summary>
    /// Open the right-click context menu for an item in a bag slot. addonId is the parent addon
    /// (e.g. the Inventory addon shown during the chocobo "Reward" prompt).
    /// </summary>
    public static bool OpenItemContextMenu(uint itemId, uint addonId)
    {
        var loc = FindItemSlot(itemId);
        if (loc == null) return false;
        try
        {
            var aic = AgentInventoryContext.Instance();
            if (aic == null) return false;
            aic->OpenForItemSlot(loc.Value.Type, loc.Value.Slot, 0, addonId);
            return true;
        }
        catch (Exception e) { Svc.Log.Verbose($"[Inventory] OpenItemContextMenu {itemId} failed: {e.Message}"); return false; }
    }

    /// <summary>Remaining seconds on a status on the local player, or 0 if absent.</summary>
    public static float GetStatusRemaining(uint statusId)
    {
        var player = ECommons.GameHelpers.Player.Object;
        if (player == null) return 0;
        foreach (var s in player.StatusList)
        {
            if (s.StatusId == statusId)
                return s.RemainingTime;
        }
        return 0;
    }

    /// <summary>True if the player currently has the "Well Fed" status (id 48).</summary>
    public static float WellFedRemaining() => GetStatusRemaining(48);

    /// <summary>True if the player currently has a Medicated status (id 49).</summary>
    public static float MedicatedRemaining() => GetStatusRemaining(49);

    public static bool HasItem(uint itemId) => GetItemCount(itemId) > 0;

    /// <summary>How many bicolor gemstones the player currently holds.</summary>
    public static int GetGemstoneCount() => GetItemCount(Data.GameItems.BicolorGemstone);

    /// <summary>Returns the highest-grade dark matter the player owns, or 0 if none.</summary>
    public static uint GetBestDarkMatter()
    {
        for (var i = Data.GameItems.DarkMatter.Length - 1; i >= 0; i--)
        {
            if (GetItemCount(Data.GameItems.DarkMatter[i]) > 0)
                return Data.GameItems.DarkMatter[i];
        }
        return 0;
    }

    public static bool HasAnyDarkMatter() => GetBestDarkMatter() != 0;

    // ------------------------------------------------- consumable discovery for the UI
    public sealed record ConsumableOption(uint ItemId, string Name, bool IsHq, int Count);

    /// <summary>
    /// Scans the player's main inventory for usable combat food and potions so the UI can
    /// present only what the user actually owns.
    /// </summary>
    public static List<ConsumableOption> ScanConsumables(bool food)
    {
        var result = new List<ConsumableOption>();
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) return result;
            var itemSheet = Svc.Data.GetExcelSheet<Item>();
            var containers = new[]
            {
                InventoryType.Inventory1, InventoryType.Inventory2,
                InventoryType.Inventory3, InventoryType.Inventory4,
            };
            var seen = new HashSet<(uint, bool)>();
            foreach (var ct in containers)
            {
                var container = im->GetInventoryContainer(ct);
                if (container == null || !container->IsLoaded) continue;
                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;
                    if (!itemSheet.TryGetRow(slot->ItemId, out var row)) continue;

                    // Food = ItemUICategory 46 ("Meal"); Potions = ItemUICategory 44 ("Medicine").
                    var cat = row.ItemUICategory.RowId;
                    var isFood = cat == 46;
                    var isPotion = cat == 44;
                    if (food && !isFood) continue;
                    if (!food && !isPotion) continue;

                    var hq = slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
                    var key = (slot->ItemId, hq);
                    if (!seen.Add(key)) continue;
                    var name = row.Name.ToString();
                    result.Add(new ConsumableOption(slot->ItemId, name, hq, GetItemCount(slot->ItemId, hq)));
                }
            }
        }
        catch (Exception e)
        {
            Svc.Log.Verbose($"[Inventory] ScanConsumables failed: {e.Message}");
        }
        return result.OrderBy(c => c.Name).ToList();
    }
}
