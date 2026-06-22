using ECommons.DalamudServices;

namespace Autofate.IPC;

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
        // BossMod backend accepts EITHER fork; show whichever is actually loaded.
        CombatBackend.BossModReborn => BossModIPC.DisplayName,
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
            // TODO(WIP): BMR autorotation preset is greyed in the UI and not wired into the flow.
            case CombatBackend.BossModReborn: BossModIPC.SetActivePreset(c.BmrPreset); break;
        }

        // BMR movement backend: just turn the AI on; /bmrai handles approach, positioning,
        // targeting, and AOE dodging itself.
        if (c.MovementBackend == CombatBackend.BossModReborn)
            BossModIPC.AiEnable(true);

        // Whenever BMR is in play (rotation OR movement/AI), push the FATE-scoping hint so BMR's
        // AutoTarget excludes mobs from fates we're not part of and respects our pull cap. Harmless
        // no-op if the preset/module isn't available.
        ApplyBmrFateTargeting(c);
    }

    /// <summary>
    /// Tell BossMod's AutoTarget to prioritize the current FATE's mobs (and ignore foreign-fate
    /// mobs) and cap MaxTargets at our mass-pull pile size. Works identically on either BossMod fork
    /// (the AutoTarget FATE/MaxTargets tracks are the same). Only acts when BossMod is a configured
    /// backend. Throttled so we don't hammer the IPC every frame.
    /// </summary>
    public static void ApplyBmrFateTargeting(Configuration c)
    {
        if (!BossModIPC.IsInstalled) return;
        if (c.RotationBackend != CombatBackend.BossModReborn
            && c.MovementBackend != CombatBackend.BossModReborn) return;
        if (!ECommons.Throttlers.EzThrottler.Throttle("AF_BmrFateTarget", 2000)) return;
        // MaxTargets 0 = unlimited; map "mass pull off" to unlimited so we don't artificially
        // throttle the rotation, and "mass pull on" to our configured pile cap.
        var maxTargets = c.MassPull ? Math.Max(1, c.MassPullMaxPile) : 0;
        BossModIPC.ApplyFateTargeting(c.BmrPreset, maxTargets);
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
    /// Hard shutdown of every combat/movement IPC, regardless of configured backend. Called on Stop
    /// so nothing is left running after a backend change or a stale BMR follow/forbid flag. Each
    /// call is independently guarded (no-ops if not installed).
    /// </summary>
    public static void ShutdownAll()
    {
        // Rotation backends.
        if (WrathComboIPC.IsInstalled) WrathComboIPC.Disable();
        if (RotationSolverIPC.IsInstalled)
        {
            RotationSolverIPC.SetOff();
            RotationSolverIPC.ResetModeCache(); // so next run re-issues "/rotation auto"
        }

        // BossMod Reborn: clear preset, disable AI, and clear any follow/forbid flags we set.
        if (BossModIPC.IsInstalled)
        {
            BossModIPC.ClearActivePreset();
            BossModIPC.AiEnable(false);
            BossModIPC.ResetAiEnableCache();      // so next run re-issues "/bmrai on"
            BossModIPC.AiForbidMovement(false);   // undo mass-pull movement takeover
            _lastBmrForbidMovement = null;        // so next run re-issues correctly
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
    /// (used during mass-pull to chase un-aggroed adds BMR won't pursue); allowing hands it back so
    /// BMR resumes AOE dodging.
    /// </summary>
    // Cache last forbid-movement state so we issue the chat command only on change (callers hammer
    // SetBmrMovement every tick; re-firing "/bmrai forbidmovement" each time spams chat).
    private static bool? _lastBmrForbidMovement;

    public static void SetBmrMovement(bool allow)
    {
        if (!BossModIPC.IsInstalled) return;
        var forbid = !allow;
        if (_lastBmrForbidMovement == forbid) return; // no change -> don't re-issue
        _lastBmrForbidMovement = forbid;
        BossModIPC.AiForbidMovement(forbid);
    }

    /// <summary>
    /// Smart-mix (BMR movement): true when we should stop our own pathing and let BMR reposition us
    /// — an AOE is about to fire or BMR's AI is actively navigating us out of danger.
    /// </summary>
    public static bool YieldMovementForDodge()
        => BossModIPC.IsInstalled && BossModIPC.ShouldYieldForDodge();

    /// <summary>True while any BMR danger is present (active forbidden zone or AI navigating).</summary>
    public static bool DangerPresent()
        => BossModIPC.IsInstalled && BossModIPC.DangerPresent();
}
