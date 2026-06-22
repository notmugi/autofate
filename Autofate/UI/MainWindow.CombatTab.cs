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
        [CombatBackend.BossModReborn] = "BossMod / BMR (autorotation)",
    };

    private void DrawCombatSection()
    {
        ImGui.TextUnformatted("Combat:");
        ImGui.TextWrapped("Pick which plugin handles your damage rotation. If a selected "
            + "plugin isn't installed, Autofate will refuse to start and tell you in chat.");

        var rot = C.RotationBackend;
        if (ImGuiEx.EnumCombo("Rotation backend", ref rot, RotationNames))
        {
            C.RotationBackend = rot; Save();
        }
        DrawInstallState(C.RotationBackend);
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
