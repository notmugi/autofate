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

    /// <summary>
    /// Hard shutdown of EVERY combat/movement IPC we ever touch, regardless of the configured
    /// backend. Called on Stop so nothing is left running if the user changed backends or we left
    /// a BMR follow/forbid flag set. Each call is independently guarded (no-ops if not installed).
    /// </summary>
    public static void ShutdownAll()
    {
        // Rotation backends.
        if (WrathComboIPC.IsInstalled) WrathComboIPC.Disable();
        if (RotationSolverIPC.IsInstalled) RotationSolverIPC.SetOff();

        // BossMod Reborn: clear preset, disable AI, and clear any follow/forbid flags we set.
        if (BossModIPC.IsInstalled)
        {
            BossModIPC.ClearActivePreset();
            BossModIPC.AiEnable(false);
            BossModIPC.ResetAiEnableCache();      // reset cache so next run re-issues "/bmrai on"
            BossModIPC.AiForbidMovement(false);   // undo mass-pull movement takeover
            _lastBmrForbidMovement = null;        // reset cache so next run re-issues correctly
            BossModIPC.AiForbidActions(false);
            BossModIPC.AiFollowTarget(false);
            BossModIPC.AiFollowCombat(false);
            BossModIPC.AiFollowOutOfCombat(false);
        }

        // Navigation.
        NavmeshIPC.Stop();
    }

    /// <summary>Whether BMR is handling movement/AOE dodging right now.</summary>
    public static bool BmrHandlesMovement(Configuration c)
        => c.MovementBackend == CombatBackend.BossModReborn && BossModIPC.IsInstalled;

    /// <summary>Is BMR installed?</summary>
    public static bool BmrInstalled => BossModIPC.IsInstalled;

    /// <summary>
    /// Allow (true) or forbid (false) BMR's AI from moving us. Forbidding hands movement to vnav
    /// (used during mass-pull so we can chase un-aggroed adds BMR won't pursue); allowing hands it
    /// back so BMR resumes AOE dodging.
    /// </summary>
    // Tracks the last forbid-movement state we sent to BMR so we only re-issue the chat command
    // when it actually changes (callers hammer SetBmrMovement every tick, and each call echoed a
    // "/bmrai forbidmovement on/off" line to chat -> spam). null = unknown/never set.
    private static bool? _lastBmrForbidMovement;

    public static void SetBmrMovement(bool allow)
    {
        if (!BossModIPC.IsInstalled) return;
        var forbid = !allow;
        if (_lastBmrForbidMovement == forbid) return; // no change -> don't re-issue / spam chat
        _lastBmrForbidMovement = forbid;
        BossModIPC.AiForbidMovement(forbid);
    }

    /// <summary>
    /// In the smart-mix (BMR movement) setup: true when we should stop our own pathing and let
    /// BMR reposition us — i.e. an AOE is about to go off or BMR's AI is actively navigating us
    /// out of danger.
    /// </summary>
    public static bool YieldMovementForDodge()
        => BossModIPC.IsInstalled && BossModIPC.ShouldYieldForDodge();

    /// <summary>True while any BMR danger is present (active forbidden zone or AI navigating).</summary>
    public static bool DangerPresent()
        => BossModIPC.IsInstalled && BossModIPC.DangerPresent();
}
