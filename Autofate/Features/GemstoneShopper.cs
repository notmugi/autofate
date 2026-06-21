using System.Linq;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using ECommons.UIHelpers.AddonMasterImplementations;
using Lumina.Excel.Sheets;

namespace Autofate.Features;

/// <summary>
/// Purchases items from a bicolor-gemstone vendor via the game's "ShopExchangeCurrency" addon,
/// using ECommons' AddonMaster for shop entries, the Select(amount) callback, and the confirm dialog.
/// The controller opens the shop; we buy up to each buy-list item's target quantity.
/// </summary>
public static unsafe class GemstoneShopper
{
    public const string ShopAddon = "ShopExchangeCurrency";
    public const string ShopDialog = "ShopExchangeCurrencyDialog";

    public static bool ShopOpen()
        => ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(ShopAddon, out var a) && a->IsVisible;

    /// <summary>
    /// True when every enabled entry has met its target quantity or is no longer affordable, i.e.
    /// nothing left we both want and can buy. The controller then returns to farming.
    /// </summary>
    public static bool BuyingComplete(Configuration c)
    {
        // Only valid once the shop is open and its item list has populated; otherwise we'd bail
        // before buying anything.
        if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(ShopAddon, out var addonPtr)
            || addonPtr == null || !addonPtr->IsVisible || !ECommons.GenericHelpers.IsAddonReady(addonPtr))
            return false;
        AddonMaster.ShopExchangeCurrency master;
        try { master = new AddonMaster.ShopExchangeCurrency((nint)addonPtr); }
        catch { return false; }
        var shopItems = master.BasicShopItems;
        if (shopItems.Length == 0) return false; // list not populated yet -> keep waiting

        var gems = InventoryUtil.GetGemstoneCount();
        foreach (var entry in c.GemstoneBuyList.Where(e => e.Enabled))
        {
            var have = InventoryUtil.GetItemCount(entry.ItemId);
            if (entry.TargetQuantity > 0 && have >= entry.TargetQuantity) continue; // already capped

            var shopItem = shopItems.FirstOrDefault(s => s.ItemId == entry.ItemId);
            if (shopItem == null || shopItem.CostAmount == 0) continue; // not sold here
            if (gems >= shopItem.CostAmount) return false;              // want it and can afford
        }
        return true; // every entry capped or unaffordable
    }



    /// <summary>
    /// Close the gemstone shop window via AtkUnitBase.Close(true) (same as pressing Escape).
    /// Returns true once it's no longer visible; caller should retry until then.
    /// </summary>
    public static bool CloseShop()
    {
        if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(ShopAddon, out var addon)
            || addon == null || !addon->IsVisible)
            return true; // already closed
        try { addon->Close(true); }
        catch (Exception e) { Svc.Log.Verbose($"[Gemstone] CloseShop failed: {e.Message}"); }
        return false; // not confirmed closed yet
    }

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
        // Continuous entries (target 0) always have something to buy; capped only if not yet met.
        var hasContinuous = c.GemstoneBuyList.Any(e => e.Enabled && e.TargetQuantity == 0);
        return hasContinuous || !AllTargetsMet(c);
    }

    /// <summary>
    /// One purchase tick against the open shop addon; call repeatedly. Returns true when there's
    /// nothing left to buy (controller then closes the shop and resumes farming).
    /// </summary>
    public static bool PurchaseTick(Configuration c)
    {
        // The "Exchange N gemstones?" confirmation is a SelectYesno — click Yes.
        // Gate on IsAddonReady to avoid NRE on the button mid-open animation.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var yn)
            && ECommons.GenericHelpers.IsAddonReady(yn))
        {
            if (EzThrottler.Throttle("AF_GemConfirm", 500))
            {
                try { new AddonMaster.SelectYesno((nint)yn).Yes(); }
                catch (Exception e) { Svc.Log.Verbose($"[Gemstone] Confirm Yes failed (mid-transition): {e.Message}"); }
            }
            return false;
        }

        // Legacy/alt confirm dialog (some currency shops use ShopExchangeCurrencyDialog).
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(ShopDialog, out var dlg) && dlg->IsVisible)
        {
            if (EzThrottler.Throttle("AF_GemConfirm", 500))
            {
                var dialog = new AddonMaster.ShopExchangeCurrencyDialog((nint)dlg);
                dialog.Exchange();
            }
            return false;
        }

        if (!ShopOpen()) return false; // shop not open yet (still loading / mid-interact) — wait, don't close

        if (!ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(ShopAddon, out var addonPtr)
            || !ECommons.GenericHelpers.IsAddonReady(addonPtr))
            return false; // addon not fully built — wait, don't treat as "done"

        var master = new AddonMaster.ShopExchangeCurrency((nint)addonPtr);
        var shopItems = master.BasicShopItems;
        // Shop just opened and its item list hasn't populated yet — DON'T return true (that would
        // instantly close the window and re-loop). Give it a few frames to fill in.
        if (shopItems.Length == 0)
        {
            if (EzThrottler.Throttle("AF_GemShopSettle", 3000))
                Svc.Log.Verbose("[Gemstone] Shop open but item list empty; waiting for it to populate.");
            return false;
        }

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
