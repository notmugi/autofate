using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Autofate.UI;

public sealed partial class MainWindow
{
    private List<Features.InventoryUtil.ConsumableOption>? _foods;
    private List<Features.InventoryUtil.ConsumableOption>? _potions;

    private void DrawConsumablesTab()
    {
        ImGui.TextWrapped("Automatically re-apply food and combat potions before they expire. "
            + "Only items currently in your inventory are listed.");
        ImGui.Separator();

        // Food
        var useFood = C.UseFood;
        if (ImGui.Checkbox("Use food", ref useFood)) { C.UseFood = useFood; Save(); }
        if (C.UseFood)
        {
            if (ImGui.Button("Refresh food list")) _foods = null;
            ImGui.SameLine();
            _foods ??= Features.InventoryUtil.ScanConsumables(true);
            DrawConsumablePicker("##foodpick", _foods, C.FoodItemId, C.FoodIsHq, (id, hq) =>
            {
                C.FoodItemId = id; C.FoodIsHq = hq; Save();
            });
            ImGui.TextDisabled($"Selected: {NameFor(C.FoodItemId, C.FoodIsHq, _foods)}");
        }

        ImGui.Separator();

        // Potion
        var usePot = C.UsePotion;
        if (ImGui.Checkbox("Use potion", ref usePot)) { C.UsePotion = usePot; Save(); }
        if (C.UsePotion)
        {
            if (ImGui.Button("Refresh potion list")) _potions = null;
            ImGui.SameLine();
            _potions ??= Features.InventoryUtil.ScanConsumables(false);
            DrawConsumablePicker("##potpick", _potions, C.PotionItemId, C.PotionIsHq, (id, hq) =>
            {
                C.PotionItemId = id; C.PotionIsHq = hq; Save();
            });
            ImGui.TextDisabled($"Selected: {NameFor(C.PotionItemId, C.PotionIsHq, _potions)}");
        }

        ImGui.Separator();
        var reuse = C.ConsumableReuseSeconds;
        if (ImGui.SliderInt("Re-apply when buff below (s)", ref reuse, 5, 300)) { C.ConsumableReuseSeconds = reuse; Save(); }
    }

    private static string NameFor(uint id, bool hq, List<Features.InventoryUtil.ConsumableOption> list)
    {
        if (id == 0) return "<none>";
        var m = list.FirstOrDefault(o => o.ItemId == id && o.IsHq == hq);
        return m != null ? $"{m.Name}{(hq ? " (HQ)" : "")}" : $"#{id}{(hq ? " (HQ)" : "")}";
    }

    private void DrawConsumablePicker(string id, List<Features.InventoryUtil.ConsumableOption> list,
        uint selId, bool selHq, Action<uint, bool> onPick)
    {
        using var combo = ImRaii.Combo(id, "Select...");
        if (!combo) return;
        if (list.Count == 0) { ImGui.TextDisabled("None in inventory."); return; }
        foreach (var o in list)
        {
            var label = $"{o.Name}{(o.IsHq ? " (HQ)" : "")}  x{o.Count}";
            if (ImGui.Selectable($"{label}##{o.ItemId}_{o.IsHq}", o.ItemId == selId && o.IsHq == selHq))
                onPick(o.ItemId, o.IsHq);
        }
    }
}
