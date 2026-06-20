using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;

namespace AutoFates.UI;

public sealed partial class MainWindow
{
    private static readonly Dictionary<ChocoboStance, string> StanceNames = new()
    {
        [ChocoboStance.Follow] = "Follow (no stance)",
        [ChocoboStance.Defender] = "Defender",
        [ChocoboStance.Attacker] = "Attacker",
        [ChocoboStance.Healer] = "Healer",
    };

    private void DrawChocoboTab()
    {
        var enabled = C.ChocoboCompanionEnabled;
        if (ImGui.Checkbox("Use chocobo companion while farming", ref enabled)) { C.ChocoboCompanionEnabled = enabled; Save(); }
        if (!C.ChocoboCompanionEnabled) return;

        ImGui.Separator();
        ImGui.TextUnformatted("Live status:");
        ImGui.TextDisabled($"Rank {Features.ChocoboManager.Rank()}, XP {Features.ChocoboManager.CurrentXP()}, "
            + $"summon {Features.ChocoboManager.TimeLeft():0}s, gysahl x{Features.InventoryUtil.GetItemCount(Data.GameItems.GysahlGreens)}");

        ImGui.Separator();
        var stance = C.ChocoboStance;
        if (ImGuiEx.EnumCombo("Stance", ref stance, StanceNames)) { C.ChocoboStance = stance; Save(); }

        var autoHeal = C.AutoHealerStance;
        if (ImGui.Checkbox("Auto-switch to Healer stance when HP is low", ref autoHeal)) { C.AutoHealerStance = autoHeal; Save(); }
        if (C.AutoHealerStance)
        {
            var thr = C.HealerStanceHpThreshold;
            if (ImGui.SliderInt("HP %% to switch to Healer", ref thr, 1, 99)) { C.HealerStanceHpThreshold = thr; Save(); }
        }

        ImGui.Separator();
        var autoGysahl = C.AutoGysahlGreens;
        if (ImGui.Checkbox("Auto re-use Gysahl Greens before companion times out", ref autoGysahl)) { C.AutoGysahlGreens = autoGysahl; Save(); }
        if (C.AutoGysahlGreens)
        {
            var s = C.GysahlReuseSeconds;
            if (ImGui.SliderInt("Re-summon when timer below (s)", ref s, 5, 300)) { C.GysahlReuseSeconds = s; Save(); }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Chocobo leveling (stable training):");
        var leveling = C.ChocoboLevelingEnabled;
        if (ImGui.Checkbox("Enable auto chocobo leveling", ref leveling)) { C.ChocoboLevelingEnabled = leveling; Save(); }
        if (C.ChocoboLevelingEnabled)
        {
            var target = C.ChocoboTargetLevel;
            if (ImGui.SliderInt("Target chocobo level", ref target, 1, 20)) { C.ChocoboTargetLevel = target; Save(); }

            var homeCmd = C.ChocoboHomeLifestreamCommand;
            if (ImGui.InputText("Home Lifestream command", ref homeCmd, 128)) { C.ChocoboHomeLifestreamCommand = homeCmd; Save(); }
            ImGui.SameLine(); Help("Lifestream command to reach your house/apartment for stabling, e.g. '/li home', '/li apartment', '/li fc'.");

            var clean = C.AutoCleanStable;
            if (ImGui.Checkbox("Auto-clean stable with Magicked Stable Brooms", ref clean)) { C.AutoCleanStable = clean; Save(); }

            ImGui.Spacing();
            ImGui.TextWrapped("Target your chocobo stable in-game, then click below to capture the exact "
                + "stable entity (most reliable). This also records the spot to navigate to.");
            if (ImGui.Button("Add targeted stable"))
            {
                var tgt = Svc.Targets.Target;
                if (tgt == null)
                {
                    Svc.Chat.PrintError("[AutoFates] No target. Click your chocobo stable first, then press this.");
                }
                else
                {
                    C.StableDataId = tgt.DataId;
                    C.StableName = tgt.Name.TextValue;
                    C.StablePosition = tgt.Position;
                    C.StableTerritory = Svc.ClientState.TerritoryType;
                    C.StablePositionSet = true;
                    Save();
                    Svc.Chat.Print($"[AutoFates] Stable added: '{C.StableName}' (DataId {C.StableDataId}).");
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Set to current spot (no entity)"))
            {
                var me = ECommons.GameHelpers.Player.Object;
                if (me != null)
                {
                    C.StablePosition = me.Position;
                    C.StableTerritory = Svc.ClientState.TerritoryType;
                    C.StableDataId = 0;
                    C.StableName = string.Empty;
                    C.StablePositionSet = true;
                    Save();
                }
            }

            if (C.StablePositionSet)
            {
                if (C.StableDataId != 0)
                    ImGui.TextDisabled($"Stable: '{C.StableName}' (DataId {C.StableDataId}) in {Data.Zones.GetTerritoryName(C.StableTerritory)} ({C.StablePosition.X:0},{C.StablePosition.Z:0})");
                else
                    ImGui.TextDisabled($"Position only in {Data.Zones.GetTerritoryName(C.StableTerritory)} ({C.StablePosition.X:0},{C.StablePosition.Z:0}) — no entity captured");
                if (ImGui.SmallButton("Clear stable")) { C.StablePositionSet = false; C.StableDataId = 0; C.StableName = string.Empty; Save(); }
            }
            else
                ImGui.TextDisabled("Stable not set");

            ImGui.TextDisabled($"Curiel Roots x{Features.InventoryUtil.GetItemCount(Data.GameItems.CurielRoot)}, "
                + $"Thavnairian Onions x{Features.InventoryUtil.GetItemCount(Data.GameItems.ThavnairianOnion)}, "
                + $"Brooms x{Features.InventoryUtil.GetItemCount(Data.GameItems.MagickedStableBroom)}");
        }
    }
}
