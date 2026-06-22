using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons.DalamudServices;

namespace Autofate.UI;

/// <summary>The main Autofate control + configuration window.</summary>
public sealed partial class MainWindow : Window
{
    private Configuration C => Plugin.C;
    private Core.FarmingController Controller => Plugin.Instance!.Controller;

    public MainWindow() : base("Autofate###AutofateMain")
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
        DrawTab("Travel", DrawTravelTab);
        DrawTab("Chocobo", DrawChocoboTab);
        DrawTab("Consumables", DrawConsumablesTab);
        DrawTab("Repair", DrawRepairTab);
        DrawTab("Gemstones", DrawGemstoneTab);
        DrawTab("Stop Triggers", DrawStopTriggersTab);

        // If a failed Start asked us to surface the error, force-select the Status tab this frame.
        var forceStatus = Controller.ForceStatusTab;
        if (forceStatus) Controller.ForceStatusTab = false; // consume the one-shot
        DrawTab("Status", DrawStatusTab, forceStatus);
    }

    private static void DrawTab(string label, Action body, bool forceSelected = false)
    {
        var flags = forceSelected ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        using var tab = ImRaii.TabItem(label, flags);
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
