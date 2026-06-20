using Dalamud.Bindings.ImGui;
using ECommons.ImGuiMethods;

namespace AutoFates.UI;

public sealed partial class MainWindow
{
    private static readonly Dictionary<CombatBackend, string> RotationNames = new()
    {
        [CombatBackend.None] = "None",
        [CombatBackend.WrathCombo] = "Wrath Combo",
        [CombatBackend.RotationSolverReborn] = "Rotation Solver Reborn",
        [CombatBackend.BossModReborn] = "BossMod Reborn (autorotation)",
    };

    private static readonly Dictionary<CombatBackend, string> MovementNames = new()
    {
        [CombatBackend.None] = "vnavmesh only (recommended)",
        [CombatBackend.BossModReborn] = "BossMod Reborn AI (movement + AOE dodge)",
    };

    private void DrawCombatTab()
    {
        ImGui.TextWrapped("Pick which plugins handle your rotation and movement. If a selected "
            + "plugin isn't installed, AutoFates will refuse to start and tell you in chat.");
        ImGui.Separator();

        // Rotation backend.
        var rot = C.RotationBackend;
        if (ImGuiEx.EnumCombo("Rotation backend", ref rot, RotationNames))
        {
            C.RotationBackend = rot; Save();
        }
        DrawInstallState(C.RotationBackend);

        // Movement backend. "vnavmesh only" is the confirmed-working default: AutoFates does its
        // own fate-mob targeting + walks into range, and the rotation backend does the damage.
        var mov = C.MovementBackend;
        if (ImGuiEx.EnumCombo("Movement / AOE backend", ref mov, m => m is CombatBackend.None or CombatBackend.BossModReborn, MovementNames))
        {
            C.MovementBackend = mov; Save();
        }
        DrawInstallState(C.MovementBackend);
        ImGui.TextDisabled("vnavmesh only: AutoFates handles targeting + positioning; rotation backend fights.");
        if (C.MovementBackend == CombatBackend.BossModReborn)
            ImGui.TextDisabled("BMR AI: requires your preset to contain the MiscAI module (NormalMovement/StayCloseToTarget).");

        if (C.MovementBackend == CombatBackend.BossModReborn || C.RotationBackend == CombatBackend.BossModReborn)
        {
            var preset = C.BmrPreset;
            if (ImGui.InputText("BMR preset name", ref preset, 128)) { C.BmrPreset = preset; Save(); }
            ImGui.SameLine(); Help("The BossMod Reborn autorotation preset to activate while farming.");
        }

        // AOE dodging when not using BMR for movement.
        if (C.MovementBackend != CombatBackend.BossModReborn)
        {
            var dodge = C.AutoDodgeAoe;
            if (ImGui.Checkbox("Auto-dodge AOEs (uses BMR hints if BMR is installed)", ref dodge)) { C.AutoDodgeAoe = dodge; Save(); }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Installed plugin detection:");
        DrawDetect("vnavmesh", IPC.NavmeshIPC.IsInstalled);
        DrawDetect("Lifestream", IPC.LifestreamIPC.IsInstalled);
        DrawDetect("BossMod Reborn", IPC.BossModIPC.IsInstalled);
        DrawDetect("Wrath Combo", IPC.WrathComboIPC.IsInstalled);
        DrawDetect("Rotation Solver Reborn", IPC.RotationSolverIPC.IsInstalled);
        DrawDetect("AutoRetainer", IPC.AutoRetainerIPC.IsInstalled);
    }

    private void DrawInstallState(CombatBackend backend)
    {
        if (backend == CombatBackend.None) return;
        var ok = IPC.IPCManager.IsBackendInstalled(backend);
        ImGui.SameLine();
        if (ok) ImGuiEx.Text(new System.Numerics.Vector4(0.3f, 0.9f, 0.3f, 1f), "installed");
        else ImGuiEx.Text(new System.Numerics.Vector4(0.9f, 0.3f, 0.3f, 1f), "NOT installed");
    }

    private static void DrawDetect(string name, bool installed)
    {
        var col = installed ? new System.Numerics.Vector4(0.3f, 0.9f, 0.3f, 1f) : new System.Numerics.Vector4(0.7f, 0.3f, 0.3f, 1f);
        ImGui.Bullet();
        ImGui.SameLine();
        ImGuiEx.Text(col, $"{name}: {(installed ? "yes" : "no")}");
    }
}
