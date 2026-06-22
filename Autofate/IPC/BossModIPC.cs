using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Reflection;

namespace Autofate.IPC;

/// <summary>
/// Wrapper over BossMod — works with EITHER fork interchangeably:
///   - awgil's vanilla "BossMod" (commands under /vbm), or
///   - CombatReborn's "BossModReborn" (commands under /bmr, AI under /bmrai).
///
/// Both forks register their IPC under the SAME "BossMod." prefix, so the Presets.* (autorotation),
/// ObstacleMap.*, and AutoTarget FATE/MaxTargets surface is identical between them. The ONLY things
/// that differ are:
///   1. the AI chat command (Reborn: "/bmrai X"; vanilla: "/vbm ai X" and "/vbm cfg AIConfig …"), and
///   2. the Hints.*/AI.* "danger" endpoints, which are Reborn-only (vanilla returns our defaults).
///
/// We auto-detect which fork is loaded and route accordingly. The user never has to pick; whichever
/// one is installed is used. (They won't have both loaded at once.)
/// </summary>
public static class BossModIPC
{
    public enum Variant { None, Reborn, Vanilla }

    // Dalamud internal names. Both expose IPC under the legacy "BossMod." prefix regardless.
    private const string RebornName = "BossModReborn";
    private const string VanillaName = "BossMod";

    /// <summary>Back-compat alias (older code referenced BossModIPC.InternalName).</summary>
    public const string InternalName = RebornName;

    /// <summary>
    /// Which fork is currently loaded. Re-resolved each access (cheap) so hot-swapping a plugin
    /// mid-session is handled. Reborn is preferred if — somehow — both are present.
    /// </summary>
    public static Variant InstalledVariant
    {
        get
        {
            if (DalamudReflector.TryGetDalamudPlugin(RebornName, out _, true, true)) return Variant.Reborn;
            if (DalamudReflector.TryGetDalamudPlugin(VanillaName, out _, true, true)) return Variant.Vanilla;
            return Variant.None;
        }
    }

    public static bool IsInstalled => InstalledVariant != Variant.None;

    /// <summary>True when the loaded fork is CombatReborn's BossModReborn.</summary>
    public static bool IsReborn => InstalledVariant == Variant.Reborn;

    /// <summary>Human-readable name of whichever fork is loaded (for UI/diagnostics).</summary>
    public static string DisplayName => InstalledVariant switch
    {
        Variant.Reborn => "BossMod Reborn",
        Variant.Vanilla => "BossMod (vanilla)",
        _ => "BossMod",
    };

    // ----------------------------------------------------- Autorotation presets
    public static bool SetActivePreset(string presetName)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive").InvokeFunc(presetName); }
        catch (Exception e) { Svc.Log.Verbose($"[BMR] SetActive failed: {e.Message}"); return false; }
    }

    public static bool ClearActivePreset()
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive").InvokeFunc(); }
        catch (Exception e) { Svc.Log.Verbose($"[BMR] ClearActive failed: {e.Message}"); return false; }
    }

    /// <summary>
    /// Activate the named autorotation preset, but ONLY if it isn't already active (change-guarded —
    /// SetActive every tick would thrash the rotation). Works on EITHER fork (shared BossMod. IPC).
    /// If the preset doesn't exist in BossMod yet, we CREATE it from our own bundled fate preset
    /// (BossModPreset.Json) so the user never has to make one by hand — same mechanism AutoDuty/BoT
    /// use (Presets.Create with serialized JSON), but our own fate-tuned module set.
    /// </summary>
    public static void EnsureActivePreset(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName)) return;
        try
        {
            if (GetActivePreset() == presetName) return; // already active

            if (GetPreset(presetName) is null)
            {
                // Not present -> create our bundled fate preset under this name.
                if (!CreatePreset(BossModPreset.Json(presetName), overwrite: true))
                {
                    if (ECommons.Throttlers.EzThrottler.Throttle("AF_BmrPresetCreateFail", 10_000))
                        Svc.Log.Warning($"[BMR] Failed to create autorotation preset '{presetName}' in {DisplayName}.");
                    return;
                }
                Svc.Log.Information($"[BMR] Created fate autorotation preset '{presetName}' in {DisplayName}.");
            }
            SetActivePreset(presetName);
        }
        catch (Exception e) { Svc.Log.Verbose($"[BMR] EnsureActivePreset failed: {e.Message}"); }
    }

    public static string? GetActivePreset()
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<string?>("BossMod.Presets.GetActive").InvokeFunc(); }
        catch { return null; }
    }

    public static bool CreatePreset(string serializedPreset, bool overwrite = true)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create").InvokeFunc(serializedPreset, overwrite); }
        catch (Exception e) { Svc.Log.Verbose($"[BMR] Create failed: {e.Message}"); return false; }
    }

    public static string? GetPreset(string name)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<string, string?>("BossMod.Presets.Get").InvokeFunc(name); }
        catch { return null; }
    }

    /// <summary>
    /// Add a transient (runtime) strategy override to a preset (drives BMR's AI to move and fight).
    /// Signature: (presetName, "BossMod.Autorotation.MiscAI.&lt;Strategy&gt;", strategyOption, value).
    /// </summary>
    public static bool AddTransientStrategy(string preset, string module, string option, string value)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<string, string, string, string, bool>("BossMod.Presets.AddTransientStrategy").InvokeFunc(preset, module, option, value); }
        catch (Exception e) { Svc.Log.Verbose($"[BMR] AddTransientStrategy failed: {e.Message}"); return false; }
    }

    // ----------------------------------------------------- AI (movement / dodge)
    // The AI mode is driven by chat commands, which DIFFER between forks:
    //   Reborn : "/bmrai on|off",  "/bmrai forbidmovement on|off",  "/bmrai follow SlotN", ...
    //   Vanilla: "/vbm ai on|off",  "/vbm cfg AIConfig ForbidMovement true|false",  "/vbm ai follow slotN", ...
    // We translate the same intent to whichever fork is loaded. Vanilla's AIConfig fields:
    //   Enabled, ForbidMovement, ForbidActions, DistanceToMaster, FollowDuringBoss.
    private static void AiCmd(string rebornArgs, string vanillaCmd)
    {
        switch (InstalledVariant)
        {
            case Variant.Reborn:
                if (!string.IsNullOrEmpty(rebornArgs)) Chat.ExecuteCommand($"/bmrai {rebornArgs}");
                break;
            case Variant.Vanilla:
                if (!string.IsNullOrEmpty(vanillaCmd)) Chat.ExecuteCommand(vanillaCmd);
                break;
        }
    }

    // Cache last state to issue the command only on change (StartCombat runs every tick; re-firing
    // the enable command each frame spams chat). null = never set.
    private static bool? _lastAiEnabled;
    public static void AiEnable(bool enable)
    {
        if (_lastAiEnabled == enable) return;
        _lastAiEnabled = enable;
        AiCmd(enable ? "on" : "off", $"/vbm ai {(enable ? "on" : "off")}");
    }

    /// <summary>Reset the cached AI-enable state (call on Stop so the next run re-issues).</summary>
    public static void ResetAiEnableCache() => _lastAiEnabled = null;

    // ----------------------------------------------------- danger detection (Hints)
    // NOTE: these Hints.*/AI.* endpoints are REBORN-ONLY. On vanilla BossMod they don't exist, so
    // the IPC subscriber throws and we return the safe default (no danger / not navigating). That's
    // fine: on vanilla we simply let BossMod's own AI own movement during fights (it dodges on its
    // own), instead of running our "vnav drives, BossMod dodges" coordination — see IPCManager.
    /// <summary>Number of active forbidden zones (incoming AOEs we should be out of). 0 on vanilla.</summary>
    public static int ForbiddenZonesCount()
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<int>("BossMod.Hints.ForbiddenZonesCount").InvokeFunc(); }
        catch { return 0; }
    }

    /// <summary>Seconds until the next forbidden zone activates (float.MaxValue if none).</summary>
    public static float ForbiddenZonesNextActivation()
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<float>("BossMod.Hints.ForbiddenZonesNextActivation").InvokeFunc(); }
        catch { return float.MaxValue; }
    }

    /// <summary>Whether BMR's AI currently wants to move us somewhere (e.g. out of an AOE).</summary>
    public static bool AiIsNavigating()
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<bool>("BossMod.AI.IsNavigating").InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// True if we should yield movement to BMR right now because danger is imminent: there is an
    /// active forbidden zone (AOE) about to go off, or BMR's AI is actively trying to reposition.
    /// </summary>
    public static bool ShouldYieldForDodge()
    {
        if (AiIsNavigating()) return true;
        return ForbiddenZonesCount() > 0 && ForbiddenZonesNextActivation() < 3f;
    }

    /// <summary>
    /// Broad "danger present" signal for the yield latch: BMR repositioning OR any active forbidden
    /// zone (regardless of timing). Broader than <see cref="ShouldYieldForDodge"/> so we don't path
    /// back into a still-active zone just because its activation timer is &gt; 3s away.
    /// </summary>
    public static bool DangerPresent()
        => AiIsNavigating() || ForbiddenZonesCount() > 0;

    /// <summary>Follow a party member by slot (0-based). Used for follow-party-leader mode. Both
    /// forks use a 1-based slot in the command (Reborn "SlotN", vanilla "slotN").</summary>
    public static void AiFollow(int slot) => AiCmd($"follow Slot{slot + 1}", $"/vbm ai follow slot{slot + 1}");

    // The following are BMR refinements with no vanilla AIConfig equivalent; they no-op on vanilla
    // (vanilla's AI handles target-following / positioning with its own defaults).
    public static void AiFollowTarget(bool enable) => AiCmd($"followtarget {(enable ? "on" : "off")}", string.Empty);

    public static void AiFollowCombat(bool enable) => AiCmd($"followcombat {(enable ? "on" : "off")}", string.Empty);

    public static void AiFollowOutOfCombat(bool enable) => AiCmd($"followoutofcombat {(enable ? "on" : "off")}", string.Empty);

    public static void AiSetMaxDistanceTarget(float dist) => AiCmd($"maxdistancetarget {dist:0.##}", string.Empty);

    public static void AiSetPositional(string positional) => AiCmd($"positional {positional}", string.Empty);

    /// <summary>Forbid / allow AI navigation (so we can hand movement to vnavmesh and back).
    /// Reborn: "/bmrai forbidmovement on|off". Vanilla: AIConfig.ForbidMovement bool.</summary>
    public static void AiForbidMovement(bool forbid)
        => AiCmd($"forbidmovement {(forbid ? "on" : "off")}", $"/vbm cfg AIConfig ForbidMovement {(forbid ? "true" : "false")}");

    // ----------------------------------------------------- FATE auto-target scoping
    // BMR's AutoTarget module (BossMod.Autorotation.MiscAI.AutoTarget) already understands fates: in
    // its AIHintsBuilder it marks any mob belonging to a fate we're NOT synced to as Invincible, so
    // they're never engaged. We just have to switch the module's "FATE" track on and (optionally)
    // cap "MaxTargets" so its own new-mob bumping respects our pull size. This is a PURE EXCLUSION
    // hint — it never reduces what we pull from our own fate; our proactive body-pull loop still
    // drives gathering. Track internal names per BMR's AutoTarget.Definition(): "FATE", "MaxTargets".
    private const string AutoTargetModule = "BossMod.Autorotation.MiscAI.AutoTarget";

    /// <summary>
    /// Push the FATE-scoping transient strategy onto the given preset: enable FATE prioritization
    /// (excludes foreign-fate mobs) and align MaxTargets with our pull cap. Best-effort; returns
    /// false if BMR rejected it (e.g. preset not found / module not registered).
    /// </summary>
    public static bool ApplyFateTargeting(string preset, int maxTargets)
    {
        var ok = AddTransientStrategy(preset, AutoTargetModule, "FATE", "Enabled");
        // MaxTargets is an int track; 0 = unlimited. Clamp negatives to 0.
        ok &= AddTransientStrategy(preset, AutoTargetModule, "MaxTargets", Math.Max(0, maxTargets).ToString());
        return ok;
    }

    /// <summary>Forbid / allow AI actions. Reborn: "/bmrai forbidactions on|off". Vanilla:
    /// AIConfig.ForbidActions bool.</summary>
    public static void AiForbidActions(bool forbid)
        => AiCmd($"forbidactions {(forbid ? "on" : "off")}", $"/vbm cfg AIConfig ForbidActions {(forbid ? "true" : "false")}");
}
