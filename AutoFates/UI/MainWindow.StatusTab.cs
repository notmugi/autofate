using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;

namespace AutoFates.UI;

public sealed partial class MainWindow
{
    private void DrawStatusTab()
    {
        var s = Controller.Stats;
        ImGui.TextUnformatted($"Running: {Controller.Running}");
        ImGui.TextUnformatted($"State: {Controller.State}");
        ImGui.TextUnformatted($"Status: {Controller.StatusText}");
        ImGui.TextUnformatted($"Last stop reason: {Controller.LastStopReason}");
        ImGui.Separator();

        ImGui.TextUnformatted($"Runtime: {s.Runtime:hh\\:mm\\:ss}");
        ImGui.TextUnformatted($"FATEs completed: {s.FatesCompleted}");
        ImGui.TextUnformatted($"FATEs attempted: {s.FatesAttempted}");
        ImGui.TextUnformatted($"Gemstones gained (gross): {s.GemstonesGained}");
        ImGui.TextUnformatted($"Level: {s.StartLevel} -> {s.CurrentLevel}");

        ImGui.Separator();
        ImGui.TextUnformatted("Current zone FATEs:");
        var candidates = Core.FateSelector.GetCandidates(C);
        if (candidates.Count == 0)
            ImGui.TextDisabled("None in range/criteria.");
        foreach (var c in candidates)
        {
            var fate = c.Fate;
            ImGui.BulletText($"{fate.Name} | {c.Type} | Lv{fate.Level} | {fate.Progress}% | {c.TimeRemaining}s | {c.Distance:0}y");
        }

        ImGui.Separator();
        var verbose = C.VerboseLogging;
        if (ImGui.Checkbox("Verbose logging", ref verbose)) { C.VerboseLogging = verbose; Save(); }
    }
}
