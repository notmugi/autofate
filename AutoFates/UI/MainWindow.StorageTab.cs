using Dalamud.Bindings.ImGui;
using ECommons.ImGuiMethods;

namespace AutoFates.UI;

public sealed partial class MainWindow
{
    private void DrawStorageTab()
    {
        ImGui.TextWrapped("Pull consumables (food, potions, gysahl greens, etc.) from your chocobo "
            + "saddlebag or retainers when you run low.");
        ImGui.Separator();

        var sb = C.UseSaddlebag;
        if (ImGui.Checkbox("Use chocobo saddlebag", ref sb)) { C.UseSaddlebag = sb; Save(); }
        if (C.UseSaddlebag)
        {
            if (Features.StorageManager.SaddlebagAvailable())
                ImGui.TextDisabled("Saddlebag is accessible.");
            else
                ImGuiEx.Text(new System.Numerics.Vector4(0.9f, 0.6f, 0.2f, 1f),
                    "Open your saddlebag once this session so the game loads it.");
        }

        ImGui.Separator();
        var ret = C.UseRetainerStorage;
        if (ImGui.Checkbox("Use retainer storage (requires AutoRetainer)", ref ret)) { C.UseRetainerStorage = ret; Save(); }
        if (C.UseRetainerStorage)
        {
            if (IPC.AutoRetainerIPC.IsInstalled)
                ImGui.TextDisabled("AutoRetainer detected. Retainer withdrawal is a work-in-progress.");
            else
                ImGuiEx.Text(new System.Numerics.Vector4(0.9f, 0.3f, 0.3f, 1f), "AutoRetainer is not installed.");
        }

        ImGui.Separator();
        ImGui.TextDisabled("Tip: AutoFates already auto-pulls Gysahl Greens, Curiel Roots, food and "
            + "potions from the saddlebag when running low (if enabled above).");
    }
}
