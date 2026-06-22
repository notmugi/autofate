using Dalamud.Bindings.ImGui;
using ECommons.ImGuiMethods;

namespace Autofate.UI;

public sealed partial class MainWindow
{
    private static readonly Dictionary<CombatBackend, string> RotationNames = new()
    {
        [CombatBackend.None] = "None",
        [CombatBackend.WrathCombo] = "Wrath Combo",
        [CombatBackend.RotationSolverReborn] = "Rotation Solver Reborn",
        [CombatBackend.BossModReborn] = "BossMod (Reborn or vanilla, autorotation)",
    };

    private static readonly Dictionary<CombatBackend, string> MovementNames = new()
    {
        [CombatBackend.None] = "vnavmesh only (recommended)",
        [CombatBackend.BossModReborn] = "BossMod AI (Reborn or vanilla — movement + AOE dodge)",
    };

    private void DrawCombatTab()
    {
        ImGui.TextWrapped("Pick which plugins handle your rotation and movement. If a selected "
            + "plugin isn't installed, Autofate will refuse to start and tell you in chat.");
        ImGui.Separator();

        var rot = C.RotationBackend;
        if (ImGuiEx.EnumCombo("Rotation backend", ref rot, r => r != CombatBackend.BossModReborn, RotationNames))
        {
            C.RotationBackend = rot; Save();
        }
        DrawInstallState(C.RotationBackend);

        // Movement is always BMR AI (forced, shown greyed).
        if (C.MovementBackend != CombatBackend.BossModReborn)
        {
            C.MovementBackend = CombatBackend.BossModReborn; Save();
        }
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled())
        {
            var mov = CombatBackend.BossModReborn;
            ImGuiEx.EnumCombo("Movement / AOE backend", ref mov, MovementNames);
        }
        DrawInstallState(CombatBackend.BossModReborn);

        // BMR autorotation preset (WIP, greyed).
        using (Dalamud.Interface.Utility.Raii.ImRaii.Disabled())
        {
            var preset = C.BmrPreset;
            ImGui.InputText("BMR preset name", ref preset, 128);
        }
        ImGui.SameLine(); ImGui.TextDisabled("(WIP)");
    }

    private void DrawInstallState(CombatBackend backend)
    {
        if (backend == CombatBackend.None) return;
        var ok = IPC.IPCManager.IsBackendInstalled(backend);
        ImGui.SameLine();
        if (ok) ImGuiEx.Text(new System.Numerics.Vector4(0.3f, 0.9f, 0.3f, 1f), "installed");
        else ImGuiEx.Text(new System.Numerics.Vector4(0.9f, 0.3f, 0.3f, 1f), "NOT installed");
    }

}
