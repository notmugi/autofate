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

    /// <summary>
    /// Shopping is COMPLETE when, for every enabled buy entry, we either:
    ///   - have reached/exceeded its target quantity (threshold met), OR
    ///   - can no longer afford another unit of it (our gemstone count &lt; the item's cost).
    /// When this is true the controller forcibly yanks state back to farming. We don't care about
    /// the buy-threshold-to-START here; only whether there's anything left we CAN and WANT to buy.
    /// </summary>
    public static bool BuyingComplete(Configuration c)
    {
        // Only meaningful while the shop is open and its item list has populated. If the shop just
        // opened and BasicShopItems is empty (it takes a few frames to fill), DO NOT report
        // complete — that would bail before we ever buy anything.
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
            // Capped entry already satisfied -> nothing more wanted for this item.
            if (entry.TargetQuantity > 0 && have >= entry.TargetQuantity) continue;

            var shopItem = shopItems.FirstOrDefault(s => s.ItemId == entry.ItemId);
            if (shopItem == null || shopItem.CostAmount == 0) continue; // not sold here -> ignore
            if (gems >= shopItem.CostAmount) return false;              // want it AND can afford -> not done
        }
        return true; // every entry is either capped or unaffordable
    }



    /// <summary>
    /// Close the currency shop window — equivalent to pressing Escape. Calls the addon's Close()
    /// (fire-callback + hide), which is exactly what the Escape key triggers for this addon.
    /// </summary>
    /// <summary>
    /// Close the gemstone (ShopExchangeCurrency) window. Mirrors GatherBuddy Reborn's
    /// VendorInteractionHelper.TryCloseShopExchangeCurrency: call AtkUnitBase.Close(true).
    /// Returns true once the window is no longer visible (caller should retry until then).
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
        // The "Exchange N gemstones for the following item?" confirmation is a SelectYesno — click
        // Yes. Gate on IsAddonReady so we don't NRE on the button mid-open animation.
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
