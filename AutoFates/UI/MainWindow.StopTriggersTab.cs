using Dalamud.Bindings.ImGui;

namespace AutoFates.UI;

public sealed partial class MainWindow
{
    private void DrawStopTriggersTab()
    {
        ImGui.TextWrapped("Automatically stop farming when one of these conditions is met.");
        ImGui.Separator();

        // Level
        var stopLvl = C.StopAtLevel;
        if (ImGui.Checkbox("Stop at desired level", ref stopLvl)) { C.StopAtLevel = stopLvl; Save(); }
        if (C.StopAtLevel)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            var lvl = C.DesiredLevel;
            if (ImGui.InputInt("##desiredlvl", ref lvl)) { C.DesiredLevel = Math.Clamp(lvl, 1, 100); Save(); }
        }

        // Gemstones
        var stopGem = C.StopAtGemstoneCount;
        if (ImGui.Checkbox("Stop at gemstone count (gross gained this session)", ref stopGem)) { C.StopAtGemstoneCount = stopGem; Save(); }
        if (C.StopAtGemstoneCount)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            var n = C.GemstoneStopCount;
            if (ImGui.InputInt("##gemstop", ref n)) { C.GemstoneStopCount = Math.Max(0, n); Save(); }
        }
        ImGui.TextDisabled($"Gross gemstones gained this session: {Controller.Stats.GemstonesGained}");

        // Chocobo maxed
        var stopChoco = C.StopAtChocoboMaxed;
        if (ImGui.Checkbox("Stop when chocobo reaches target level", ref stopChoco)) { C.StopAtChocoboMaxed = stopChoco; Save(); }

        // Vendor requirement
        var stopVendor = C.StopAtVendorRequirementMet;
        if (ImGui.Checkbox("Stop when gemstone buy-list targets are all met", ref stopVendor)) { C.StopAtVendorRequirementMet = stopVendor; Save(); }
        ImGui.SameLine(); Help("Only counts if every enabled buy-list entry has a fixed target quantity (not continuous).");
    }
}
