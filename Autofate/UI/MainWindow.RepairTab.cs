using Dalamud.Bindings.ImGui;
using ECommons.ImGuiMethods;

namespace Autofate.UI;

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

        // Force self-repair (Mender NPC is WIP: greyed and unselectable).
        if (C.RepairMode != RepairMode.SelfRepair) { C.RepairMode = RepairMode.SelfRepair; Save(); }
        var mode = C.RepairMode;
        if (ImGuiEx.EnumCombo("Repair method", ref mode, m => m == RepairMode.SelfRepair, RepairModeNames)) { C.RepairMode = mode; Save(); }
        ImGui.SameLine(); ImGui.TextDisabled("(Mender: WIP)");

        var dm = Features.InventoryUtil.GetBestDarkMatter();
        if (dm == 0)
            ImGuiEx.Text(new System.Numerics.Vector4(0.9f, 0.5f, 0.2f, 1f), "No Dark Matter! Farming will stop if repair is needed.");

        ImGui.Separator();
        ImGui.TextUnformatted($"Current lowest durability: {Features.RepairManager.GetLowestDurabilityPercent()}%");
    }
}
