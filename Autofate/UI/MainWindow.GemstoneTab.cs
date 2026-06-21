using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Autofate.UI;

public sealed partial class MainWindow
{
    private void DrawGemstoneTab()
    {
        ImGui.TextWrapped("Spend bicolor gemstones at a vendor automatically. When you reach the "
            + "threshold, Autofate buys your list. Open the gemstone vendor to import its offerings.");
        ImGui.Separator();

        var enable = C.EnableGemstoneShopping;
        if (ImGui.Checkbox("Enable gemstone shopping", ref enable)) { C.EnableGemstoneShopping = enable; Save(); }
        if (!C.EnableGemstoneShopping) return;

        var thr = C.GemstoneBuyThreshold;
        if (ImGui.InputInt("Buy when gemstones reach", ref thr)) { C.GemstoneBuyThreshold = Math.Max(0, thr); Save(); }
        ImGui.SameLine(); Help("Autofate heads to the vendor once you hold at least this many bicolor gemstones.");

        ImGui.TextDisabled($"You currently hold {Features.InventoryUtil.GetGemstoneCount()} bicolor gemstones.");

        if (C.VendorPositionSet)
        {
            ImGui.TextDisabled($"Vendor zone: {Data.Zones.GetTerritoryName(C.VendorTerritory)} — will teleport to the nearest aetheryte.");
            if (ImGui.SmallButton("Clear vendor location")) { C.VendorPositionSet = false; C.VendorDataId = 0; C.VendorName = string.Empty; C.VendorTerritory = 0; Save(); }
        }
        else
            ImGui.TextDisabled("Vendor zone not set — add an item below while the vendor is open to capture it.");

        ImGui.Separator();
        ImGui.TextUnformatted("Buy list:");

        GemstoneBuyEntry? toRemove = null;
        using (var tbl = ImRaii.Table("##gembuylist", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            if (tbl)
            {
                ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Target (0=∞)", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Have", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("##act", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableHeadersRow();

                foreach (var e in C.GemstoneBuyList)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    var en = e.Enabled;
                    if (ImGui.Checkbox($"##en{e.GetHashCode()}", ref en)) { e.Enabled = en; Save(); }
                    ImGui.SameLine();
                    ImGui.TextUnformatted(e.Name);

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    var q = e.TargetQuantity;
                    if (ImGui.InputInt($"##q{e.GetHashCode()}", ref q)) { e.TargetQuantity = Math.Max(0, q); Save(); }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{Features.InventoryUtil.GetItemCount(e.ItemId)}");

                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"Remove##{e.GetHashCode()}")) toRemove = e;
                }
            }
        }
        if (toRemove != null) { C.GemstoneBuyList.Remove(toRemove); Save(); }

        ImGui.Spacing();

        // Import from an open vendor.
        if (Features.GemstoneShopper.ShopOpen())
        {
            ImGui.TextUnformatted("Vendor is open — add items it sells:");
            using var box = ImRaii.Child("##vendoroffer", new System.Numerics.Vector2(0, 180), true);
            foreach (var offer in Features.GemstoneShopper.GetCurrentShopOffer())
            {
                var already = C.GemstoneBuyList.Any(e => e.ItemId == offer.ItemId);
                if (already) { ImGui.TextDisabled($"{offer.Name} ({offer.Cost}g) - added"); continue; }
                if (ImGui.SmallButton($"Add##{offer.ItemId}"))
                {
                    C.GemstoneBuyList.Add(new GemstoneBuyEntry { ItemId = offer.ItemId, Name = offer.Name, TargetQuantity = 0 });
                    // Record vendor zone + position to navigate back later.
                    CaptureVendorLocationFromHere();
                    Save();
                }
                ImGui.SameLine();
                ImGui.TextUnformatted($"{offer.Name}  ({offer.Cost} gemstones)");
            }
        }
        else
        {
            ImGui.TextDisabled("Open the bicolor gemstone vendor in-game to import its item list here.");
        }
    }

    /// <summary>Record the vendor's zone and position so we can navigate back later.</summary>
    private void CaptureVendorLocationFromHere()
    {
        var me = ECommons.GameHelpers.Player.Object;
        if (me == null) return;
        // Prefer the targeted NPC; else use our position.
        var tgt = ECommons.DalamudServices.Svc.Targets.Target;
        if (tgt != null)
        {
            C.VendorDataId = tgt.BaseId;
            C.VendorName = tgt.Name.TextValue;
            C.VendorPosition = tgt.Position;
        }
        else
        {
            C.VendorDataId = 0;
            C.VendorName = string.Empty;
            C.VendorPosition = me.Position;
        }
        C.VendorTerritory = ECommons.DalamudServices.Svc.ClientState.TerritoryType;
        C.VendorPositionSet = true;
        ECommons.DalamudServices.Svc.Chat.Print($"[Autofate] Gemstone vendor zone captured: {Data.Zones.GetTerritoryName(C.VendorTerritory)}.");
    }
}
