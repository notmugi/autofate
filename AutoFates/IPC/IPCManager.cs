using ECommons.DalamudServices;

namespace AutoFates.IPC;

/// <summary>
/// Central coordinator for all inter-plugin communication. Exposes a single combat abstraction
/// over the three supported backends (BMR / WrathCombo / RSR) and validates that the user's
/// chosen backends are actually installed.
/// </summary>
public static class IPCManager
{
    private static WrathComboCallbackReceiver? _wrathCallback;

    public static void Init()
    {
        // WrathCombo needs EzIPC init + a callback receiver so it can notify us on lease loss.
        if (WrathComboIPC.IsInstalled)
        {
            WrathComboIPC.Init();
            _wrathCallback = new WrathComboCallbackReceiver();
        }
    }

    public static void Dispose()
    {
        WrathComboIPC.Dispose();
        _wrathCallback = null;
    }

    public static bool IsBackendInstalled(CombatBackend backend) => backend switch
    {
        CombatBackend.BossModReborn => BossModIPC.IsInstalled,
        CombatBackend.WrathCombo => WrathComboIPC.IsInstalled,
        CombatBackend.RotationSolverReborn => RotationSolverIPC.IsInstalled,
        _ => false,
    };

    public static string BackendName(CombatBackend backend) => backend switch
    {
        CombatBackend.BossModReborn => "BossMod Reborn",
        CombatBackend.WrathCombo => "Wrath Combo",
        CombatBackend.RotationSolverReborn => "Rotation Solver Reborn",
        CombatBackend.None => "None",
        _ => "Unknown",
    };

    /// <summary>
    /// Validates the configured backends are present. Returns false (and prints to chat) if a
    /// selected backend is missing or none is selected.
    /// </summary>
    public static bool ValidateBackends(Configuration c, out string error)
    {
        error = string.Empty;

        if (c.RotationBackend == CombatBackend.None && c.MovementBackend == CombatBackend.None)
        {
            error = "No combat plugin selected. Choose a rotation and/or movement backend (BMR, Wrath, or RSR) in the Combat tab.";
            return false;
        }

        if (c.RotationBackend != CombatBackend.None && !IsBackendInstalled(c.RotationBackend))
        {
            error = $"Selected rotation backend '{BackendName(c.RotationBackend)}' is not installed/loaded.";
            return false;
        }

        if (c.MovementBackend != CombatBackend.None && !IsBackendInstalled(c.MovementBackend))
        {
            error = $"Selected movement backend '{BackendName(c.MovementBackend)}' is not installed/loaded.";
            return false;
        }

        if (!NavmeshIPC.IsInstalled && !c.FollowPartyLeader && c.MovementBackend != CombatBackend.BossModReborn)
        {
            error = "vnavmesh is required for navigation but is not installed/loaded.";
            return false;
        }

        return true;
    }

    // ----------------------------------------------------- unified combat control
    /// <summary>Engage the configured combat backends (rotation + movement/dodge).</summary>
    public static void StartCombat(Configuration c)
    {
        switch (c.RotationBackend)
        {
            case CombatBackend.WrathCombo: WrathComboIPC.Enable(); break;
            case CombatBackend.RotationSolverReborn: RotationSolverIPC.SetAuto(); break;
            case CombatBackend.BossModReborn: BossModIPC.SetActivePreset(c.BmrPreset); break;
        }

        // BMR movement backend: just turn the AI on. /bmrai handles approach, positioning,
        // targeting, and AOE dodging by itself.
        if (c.MovementBackend == CombatBackend.BossModReborn)
            BossModIPC.AiEnable(true);
    }

    public static void StopCombat(Configuration c)
    {
        switch (c.RotationBackend)
        {
            case CombatBackend.WrathCombo: WrathComboIPC.Disable(); break;
            case CombatBackend.RotationSolverReborn: RotationSolverIPC.SetOff(); break;
            case CombatBackend.BossModReborn: BossModIPC.ClearActivePreset(); break;
        }

        if (c.MovementBackend == CombatBackend.BossModReborn)
            BossModIPC.AiEnable(false);
    }

    /// <summary>Whether BMR is handling movement/AOE dodging right now.</summary>
    public static bool BmrHandlesMovement(Configuration c)
        => c.MovementBackend == CombatBackend.BossModReborn && BossModIPC.IsInstalled;
}
