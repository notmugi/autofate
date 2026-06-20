using Dalamud.Bindings.ImGui;

namespace AutoFates.UI;

public sealed partial class MainWindow
{
    private void DrawFateEngineTab()
    {
        ImGui.TextWrapped("Tune how the plugin picks and runs FATEs.");
        ImGui.Separator();

        // Fate type toggles.
        ImGui.TextUnformatted("Enabled FATE types:");
        DrawFateTypeToggle("Battle", FateType.Battle);
        DrawFateTypeToggle("Boss", FateType.Boss);
        DrawFateTypeToggle("Collect", FateType.Collect);
        DrawFateTypeToggle("Defend", FateType.Defend);
        DrawFateTypeToggle("Escort", FateType.Escort);

        ImGui.Separator();

        var levelsAbove = C.LevelsAbovePlayer;
        if (ImGui.InputInt("Levels above player to still run", ref levelsAbove))
        {
            C.LevelsAbovePlayer = Math.Clamp(levelsAbove, 0, 50);
            Save();
        }
        ImGui.SameLine(); Help("Skip FATEs more than this many levels above your current level. Default 2.");

        var minTime = C.MinFateTimeSeconds;
        if (ImGui.InputInt("Min seconds remaining", ref minTime))
        {
            C.MinFateTimeSeconds = Math.Clamp(minTime, 0, 3600);
            Save();
        }
        ImGui.SameLine(); Help("Ignore FATEs with less than this many seconds left on their timer.");

        var prio = C.PrioritizeLowTimer;
        if (ImGui.Checkbox("Prioritize FATEs lower on their timer (over closest)", ref prio))
        {
            C.PrioritizeLowTimer = prio; Save();
        }

        var sync = C.AutoLevelSync;
        if (ImGui.Checkbox("Auto level-sync to the target FATE", ref sync))
        {
            C.AutoLevelSync = sync; Save();
        }
        ImGui.SameLine(); Help("Only syncs once you arrive inside the target FATE, so you don't sync to fates you pass through.");

        ImGui.Separator();
        ImGui.TextUnformatted("Engagement:");

        var massPull = C.MassPull;
        if (ImGui.Checkbox("Mass-pull enemies in the area", ref massPull)) { C.MassPull = massPull; Save(); }

        var dodge = C.AutoDodgeAoe;
        if (ImGui.Checkbox("Auto-dodge AOEs (when not using BMR for movement)", ref dodge)) { C.AutoDodgeAoe = dodge; Save(); }

        var safe = C.SafeDistance;
        if (ImGui.SliderFloat("Safe distance from non-fate enemies (yalms)", ref safe, 0, 30))
        {
            C.SafeDistance = safe; Save();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Collect FATEs:");
        var initial = C.CollectInitialTurnIn;
        if (ImGui.InputInt("Initial turn-in batch", ref initial))
        {
            C.CollectInitialTurnIn = Math.Clamp(initial, 1, 99);
            Save();
        }
        ImGui.SameLine(); Help("We turn in this many first, then use the progress delta to estimate how many more are needed.");
    }

    private void DrawFateTypeToggle(string label, FateType flag)
    {
        var on = (C.EnabledFateTypes & flag) != 0;
        if (ImGui.Checkbox(label, ref on))
        {
            if (on) C.EnabledFateTypes |= flag;
            else C.EnabledFateTypes &= ~flag;
            Save();
        }
        ImGui.SameLine();
    }

    private static void Help(string text)
    {
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(320);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
