using System.Linq;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using Lumina.Excel.Sheets;

namespace AutoFates.Features;

/// <summary>
/// Handles purchasing items from a bicolor-gemstone currency vendor.
///
/// The vendor uses the game's "ShopExchangeCurrency" addon. ECommons' AddonMaster gives us the
/// shop entries (item id / index / cost) and a Select(amount) callback, plus a confirm dialog.
///
/// Flow (driven by the controller):
///  1. Controller navigates to the chosen vendor NPC and interacts (opening the shop addon).
///  2. We read the current inventory, compute how many of each buy-list item we still need,
///     and purchase the difference (respecting per-item target quantities).
///  3. Auto-bought gemstone spend is NOT counted against the session gemstone total (the
///     controller tracks gross gemstones gained from fates separately).
/// </summary>
public static unsafe class GemstoneShopper
{
    public const string ShopAddon = "ShopExchangeCurrency";
    public const string ShopDialog = "ShopExchangeCurrencyDialog";

    public static bool ShopOpen()
        => ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(ShopAddon, out var a) && a->IsVisible;

    /// <summary>True if every enabled buy-list entry has met its target quantity.</summary>
    public static bool AllTargetsMet(Configuration c)
    {
        foreach (var entry in c.GemstoneBuyList.Where(e => e.Enabled && e.TargetQuantity > 0))
        {
            if (InventoryUtil.GetItemCount(entry.ItemId) < entry.TargetQuantity)
                return false;
        }
        return true;
    }

    /// <summary>Should we head to the vendor? (threshold reached and something still to buy)</summary>
    public static bool ShouldShop(Configuration c)
    {
        if (!c.EnableGemstoneShopping) return false;
        if (c.GemstoneBuyList.Count == 0) return false;
        if (InventoryUtil.GetGemstoneCount() < c.GemstoneBuyThreshold) return false;
        // Continuous-buy entries (target 0) always have "something to buy"; capped entries
        // only if not yet met.
        var hasContinuous = c.GemstoneBuyList.Any(e => e.Enabled && e.TargetQuantity == 0);
        return hasContinuous || !AllTargetsMet(c);
    }

    /// <summary>
    /// Perform one purchase tick against the open shop addon. Returns true when there is nothing
    /// left to buy (so the controller can close the shop and resume farming).
    /// Should be called repeatedly while the shop is open.
    /// </summary>
    public static bool PurchaseTick(Configuration c)
    {
        if (!ShopOpen()) return true; // shop not open; nothing to do here

        // If a confirm dialog is up, confirm it first.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(ShopDialog, out var dlg) && dlg->IsVisible)
        {
            if (EzThrottler.Throttle("AF_GemConfirm", 500))
            {
                var dialog = new AddonMaster.ShopExchangeCurrencyDialog((nint)dlg);
                dialog.Exchange();
            }
            return false;
        }

        if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(ShopAddon, out var addonPtr))
            return false;

        var master = new AddonMaster.ShopExchangeCurrency((nint)addonPtr);
        var shopItems = master.BasicShopItems;
        if (shopItems.Length == 0) return true; // nothing in this shop

        var gems = InventoryUtil.GetGemstoneCount();

        foreach (var entry in c.GemstoneBuyList.Where(e => e.Enabled))
        {
            // Continuous (target 0) -> buy while affordable; capped -> until target met.
            var have = InventoryUtil.GetItemCount(entry.ItemId);
            if (entry.TargetQuantity > 0 && have >= entry.TargetQuantity) continue;

            var shopItem = shopItems.FirstOrDefault(s => s.ItemId == entry.ItemId);
            if (shopItem == null) continue; // this vendor doesn't sell it
            if (shopItem.CostAmount == 0 || gems < shopItem.CostAmount) continue; // can't afford

            // How many can we buy this tick?
            var affordable = (int)(gems / shopItem.CostAmount);
            var want = entry.TargetQuantity > 0 ? entry.TargetQuantity - have : affordable;
            var buy = Math.Min(want, affordable);
            if (buy <= 0) continue;

            if (!EzThrottler.Throttle("AF_GemBuy", 800)) return false;
            shopItem.Select(buy);
            Svc.Log.Debug($"[Gemstone] Buying {buy}x {entry.Name} ({entry.ItemId}).");
            return false; // one purchase per tick; loop again next frame
        }

        // Nothing left to buy.
        return true;
    }

    /// <summary>
    /// Returns the list of items the configured vendor offers (only valid while the shop is open).
    /// Useful for populating the buy-list UI from a live vendor.
    /// </summary>
    public static List<(uint ItemId, string Name, uint Cost)> GetCurrentShopOffer()
    {
        var result = new List<(uint, string, uint)>();
        if (!ShopOpen()) return result;
        if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(ShopAddon, out var addonPtr))
            return result;
        try
        {
            var master = new AddonMaster.ShopExchangeCurrency((nint)addonPtr);
            var sheet = Svc.Data.GetExcelSheet<Item>();
            foreach (var s in master.BasicShopItems)
            {
                var name = sheet.TryGetRow(s.ItemId, out var row) ? row.Name.ToString() : $"#{s.ItemId}";
                result.Add((s.ItemId, name, s.CostAmount));
            }
        }
        catch (Exception e) { Svc.Log.Verbose($"[Gemstone] GetCurrentShopOffer failed: {e.Message}"); }
        return result;
    }
}
