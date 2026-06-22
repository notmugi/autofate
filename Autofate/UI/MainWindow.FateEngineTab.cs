using Dalamud.Bindings.ImGui;

namespace Autofate.UI;

public sealed partial class MainWindow
{
    private void DrawFateEngineTab()
    {
        // Combat section first (rotation backend + fixed hybrid movement).
        DrawCombatSection();
        ImGui.Separator();

        ImGui.TextWrapped("Tune how the plugin picks and runs FATEs. Use the blacklist below to skip specific FATEs.");
        ImGui.Separator();

        const float numWidth = 90f;

        var levelsAbove = C.LevelsAbovePlayer;
        ImGui.SetNextItemWidth(numWidth);
        if (ImGui.InputInt("Levels above player to still run", ref levelsAbove))
        {
            C.LevelsAbovePlayer = Math.Clamp(levelsAbove, 0, 50);
            Save();
        }
        ImGui.SameLine(); Help("Skip FATEs more than this many levels above your current level. Default 2.");

        var minTime = C.MinFateTimeSeconds;
        ImGui.SetNextItemWidth(numWidth);
        if (ImGui.InputInt("Min seconds remaining", ref minTime))
        {
            C.MinFateTimeSeconds = Math.Clamp(minTime, 0, 3600);
            Save();
        }
        ImGui.SameLine(); Help("Ignore FATEs with less than this many seconds left on their timer.");

        var dwell = C.ZoneDwellSeconds;
        ImGui.SetNextItemWidth(numWidth);
        if (ImGui.InputInt("Zone dwell (seconds)", ref dwell))
        {
            C.ZoneDwellSeconds = Math.Clamp(dwell, 0, 3600);
            Save();
        }
        ImGui.SameLine(); Help("In multi-zone modes, how long to wait for FATEs to respawn in a zone "
            + "before rotating to another zone. FATEs pop every few minutes, so keep this high (e.g. 240) "
            + "to avoid teleport-hopping. 0 = never rotate (stay in one zone forever).");

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
        if (C.MassPull)
        {
            ImGui.Indent();
            var maxPile = C.MassPullMaxPile;
            if (ImGui.SliderInt("Max enemies to hold at once", ref maxPile, 1, 20))
            {
                C.MassPullMaxPile = maxPile; Save();
            }
            ImGui.Unindent();
        }


        ImGui.Separator();
        ImGui.TextUnformatted("Party:");
        var followLeader = C.FollowPartyLeader;
        if (ImGui.Checkbox("Follow party leader (don't navigate to fates)", ref followLeader))
        {
            C.FollowPartyLeader = followLeader; Save();
        }
        ImGui.SameLine(); Help("Instead of picking and pathing to our own FATEs, just follow the party leader and run whatever FATE they drop us in. Good for multiboxing or farming with friends. Uses BossMod's native follow when BossMod handles movement, otherwise vnavmesh.");
        if (C.FollowPartyLeader)
        {
            ImGui.Indent();
            var followDist = C.FollowDistance;
            if (ImGui.SliderFloat("Follow distance (yalms)", ref followDist, 1f, 15f))
            {
                C.FollowDistance = followDist; Save();
            }
            ImGui.Unindent();
        }

        DrawFateBlacklist();
    }

    private void DrawFateBlacklist()
    {
        ImGui.Separator();
        ImGui.TextUnformatted("FATE blacklist:");
        ImGui.Spacing();

        if (C.FateBlacklist.Count > 0)
        {
            string? remove = null;
            foreach (var name in C.FateBlacklist)
            {
                if (ImGui.SmallButton($"X##bl_{name}")) remove = name;
                ImGui.SameLine();
                ImGui.TextUnformatted(name);
            }
            if (remove != null) { C.FateBlacklist.Remove(remove); Save(); }
        }
        else
        {
            ImGui.TextDisabled("(none)");
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Seen this session (click to blacklist):");
        using var box = Dalamud.Interface.Utility.Raii.ImRaii.Child("##seenfates",
            new System.Numerics.Vector2(0, 140), true);
        if (box)
        {
            if (Core.FateSelector.SeenFateNames.Count == 0)
                ImGui.TextDisabled("No FATEs seen yet.");
            foreach (var name in Core.FateSelector.SeenFateNames)
            {
                var blacklisted = C.FateBlacklist.Contains(name);
                if (blacklisted) continue;
                if (ImGui.Selectable(name))
                {
                    C.FateBlacklist.Add(name);
                    Save();
                }
            }
        }
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
