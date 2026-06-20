using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;

namespace AutoFates.UI;

/// <summary>The main AutoFates control + configuration window.</summary>
public sealed partial class MainWindow : Window
{
    private Configuration C => Plugin.C;
    private Core.FarmingController Controller => Plugin.Instance!.Controller;

    public MainWindow() : base("AutoFates###AutoFatesMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 420),
            MaximumSize = new Vector2(1200, 1400),
        };
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Separator();

        using var tabs = ImRaii.TabBar("##aftabs");
        if (!tabs) return;

        DrawTab("Mode", DrawModeTab);
        DrawTab("Fate Engine", DrawFateEngineTab);
        DrawTab("Combat", DrawCombatTab);
        DrawTab("Travel", DrawTravelTab);
        DrawTab("Chocobo", DrawChocoboTab);
        DrawTab("Consumables", DrawConsumablesTab);
        DrawTab("Repair", DrawRepairTab);
        DrawTab("Gemstones", DrawGemstoneTab);
        DrawTab("Storage", DrawStorageTab);
        DrawTab("Stop Triggers", DrawStopTriggersTab);
        DrawTab("Status", DrawStatusTab);
    }

    private static void DrawTab(string label, Action body)
    {
        using var tab = ImRaii.TabItem(label);
        if (!tab) return;
        using var child = ImRaii.Child($"##child_{label}", new Vector2(0, 0), false);
        body();
    }

    private void DrawHeader()
    {
        var running = Controller.Running;
        var btnColor = running ? new Vector4(0.7f, 0.2f, 0.2f, 1f) : new Vector4(0.2f, 0.6f, 0.2f, 1f);
        using (ImRaii.PushColor(ImGuiCol.Button, btnColor))
        {
            if (ImGui.Button(running ? "STOP" : "START", new Vector2(120, 32)))
                Controller.Toggle();
        }
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted($"State: {Controller.State}");
        ImGui.TextUnformatted(Controller.StatusText);
        ImGui.EndGroup();

        ImGui.SameLine(0, 30);
        ImGui.BeginGroup();
        ImGui.TextUnformatted($"Fates: {Controller.Stats.FatesCompleted}");
        ImGui.TextUnformatted($"Gems: {Controller.Stats.GemstonesGained}");
        ImGui.EndGroup();
    }

    private void Save() => C.Save();
}
