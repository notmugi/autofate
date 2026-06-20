using Dalamud.Bindings.ImGui;
using ECommons.ImGuiMethods;

namespace AutoFates.UI;

public sealed partial class MainWindow
{
    private static readonly Dictionary<RepairMode, string> RepairModeNames = new()
    {
        [RepairMode.SelfRepair] = "Self repair (Dark Matter)",
        [RepairMode.NpcRepair] = "Mender NPC",
    };

    private void DrawRepairTab()
    {
        var auto = C.AutoRepair;
        if (ImGui.Checkbox("Auto repair gear", ref auto)) { C.AutoRepair = auto; Save(); }
        if (!C.AutoRepair) return;

        ImGui.Separator();
        var thr = C.RepairThresholdPercent;
        if (ImGui.SliderInt("Repair when durability below (%%)", ref thr, 1, 99)) { C.RepairThresholdPercent = thr; Save(); }

        var mode = C.RepairMode;
        if (ImGuiEx.EnumCombo("Repair method", ref mode, RepairModeNames)) { C.RepairMode = mode; Save(); }

        if (C.RepairMode == RepairMode.SelfRepair)
        {
            var gs = C.RepairGearsetNumber;
            if (ImGui.InputInt("Crafter gearset number", ref gs)) { C.RepairGearsetNumber = Math.Clamp(gs, 1, 100); Save(); }
            ImGui.SameLine(); Help("AutoFates swaps to this gearset to self-repair (it needs the right craftsmanship/control stats), then swaps back.");

            ImGui.Spacing();
            var dm = Features.InventoryUtil.GetBestDarkMatter();
            if (dm != 0) ImGui.TextDisabled("Dark Matter available.");
            else ImGuiEx.Text(new System.Numerics.Vector4(0.9f, 0.5f, 0.2f, 1f), "No Dark Matter! Farming will stop if repair is needed.");
        }
        else
        {
            ImGui.TextDisabled("NPC repair navigation is a work-in-progress; self-repair is recommended for now.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted($"Current lowest durability: {Features.RepairManager.GetLowestDurabilityPercent()}%");
    }
}
