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

        if (c.RotationBackend == CombatBackend.None)
        {
            error = "No rotation plugin selected. Choose a rotation backend (BMR, Wrath, or RSR) in the Combat tab.";
            return false;
        }

        if (!IsBackendInstalled(c.RotationBackend))
        {
            error = $"Selected rotation backend '{BackendName(c.RotationBackend)}' is not installed/loaded.";
            return false;
        }

        // Movement is the fixed hybrid (vnavmesh + BossMod/BMR AI), so both are required.
        if (!NavmeshIPC.IsInstalled)
        {
            error = "vnavmesh is required for navigation but is not installed/loaded.";
            return false;
        }

        if (!BossModIPC.IsInstalled)
        {
            error = "BossMod (or BossMod Reborn) is required for in-combat movement / AOE dodging but is not installed/loaded.";
            return false;
        }

        // Lifestream is required for travel/teleport between zones and home (chocobo stabling).
        if (!LifestreamIPC.IsInstalled)
        {
            error = "Lifestream is required for travel/teleport but is not installed/loaded.";
            return false;
        }

        // TextAdvance is required for all dialogue handling (fate start, collect turn-ins, cutscenes).
        if (!TextAdvanceIPC.IsInstalled)
        {
            error = "TextAdvance is required for dialogue handling but is not installed/loaded.";
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
            // BossMod/BMR autorotation: activate the user's named preset on whichever fork is loaded
            // (the BossMod. IPC prefix is shared by both). Change-guarded inside EnsureActivePreset
            // so we only re-issue SetActive when it isn't already the active preset.
            case CombatBackend.BossModReborn: BossModIPC.EnsureActivePreset(BossModPreset.Name); break;
        }

        // Movement is always the hybrid: turn BMR's AI on so it handles in-combat repositioning +
        // AOE dodging (vnav handles travel/approach via Navigator).
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
        // BMR is always in play (it's the fixed movement/AOE backend), so always apply.
        if (!ECommons.Throttlers.EzThrottler.Throttle("AF_BmrFateTarget", 2000)) return;
        // MaxTargets 0 = unlimited; map "mass pull off" to unlimited so we don't artificially
        // throttle the rotation, and "mass pull on" to our configured pile cap.
        var maxTargets = c.MassPull ? Math.Max(1, c.MassPullMaxPile) : 0;
        BossModIPC.ApplyFateTargeting(BossModPreset.Name, maxTargets);
    }

    public static void StopCombat(Configuration c)
    {
        switch (c.RotationBackend)
        {
            case CombatBackend.WrathCombo: WrathComboIPC.Disable(); break;
            case CombatBackend.RotationSolverReborn: RotationSolverIPC.SetOff(); break;
            case CombatBackend.BossModReborn: BossModIPC.ClearActivePreset(); break;
        }

        // Movement is always BMR-AI hybrid -> always turn it off on stop.
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

    /// <summary>Whether BMR is handling movement/AOE dodging right now. Movement is the fixed hybrid,
    /// so this is true whenever BossMod/BMR is installed.</summary>
    public static bool BmrHandlesMovement(Configuration c)
        => BossModIPC.IsInstalled;

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
