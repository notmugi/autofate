using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Reflection;

namespace AutoFates.IPC;

/// <summary>
/// Wrapper over BossMod Reborn. Provides:
///  - Autorotation preset control via "BossMod.Presets.*" IPC funcs.
///  - AI (movement + AOE dodging + targeting) via the /bmrai text command interface.
/// BMR's AI is what gives us automatic dodging and navigation; the autorotation preset
/// drives the actual combat actions.
/// </summary>
public static class BossModIPC
{
    // The plugin's Dalamud internal name is "BossModReborn", but its IPC endpoints are still
    // registered under the "BossMod." prefix (inherited from the original BossMod).
    public const string InternalName = "BossModReborn";
    private const string LegacyInternalName = "BossMod";

    public static bool IsInstalled
        => DalamudReflector.TryGetDalamudPlugin(InternalName, out _, true, true)
           || DalamudReflector.TryGetDalamudPlugin(LegacyInternalName, out _, true, true);

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
    /// Add a transient (runtime) strategy override to a preset. This is how AutoDuty drives BMR's
    /// AI to actually move and fight, e.g. NormalMovement=Pathfind and StayCloseToTarget=range.
    /// Signature: (presetName, "BossMod.Autorotation.MiscAI.&lt;Strategy&gt;", strategyOption, value).
    /// </summary>
    public static bool AddTransientStrategy(string preset, string module, string option, string value)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<string, string, string, string, bool>("BossMod.Presets.AddTransientStrategy").InvokeFunc(preset, module, option, value); }
        catch (Exception e) { Svc.Log.Verbose($"[BMR] AddTransientStrategy failed: {e.Message}"); return false; }
    }

    // ----------------------------------------------------- AI (movement / dodge)
    // BMR AI is controlled with the /bmrai chat command.
    public static void AiEnable(bool enable) => Chat.ExecuteCommand($"/bmrai {(enable ? "on" : "off")}");

    /// <summary>Follow a party member by slot (0-based). Used for follow-party-leader mode.</summary>
    public static void AiFollow(int slot) => Chat.ExecuteCommand($"/bmrai follow Slot{slot + 1}");

    public static void AiFollowTarget(bool enable) => Chat.ExecuteCommand($"/bmrai followtarget {(enable ? "on" : "off")}");

    public static void AiFollowCombat(bool enable) => Chat.ExecuteCommand($"/bmrai followcombat {(enable ? "on" : "off")}");

    public static void AiFollowOutOfCombat(bool enable) => Chat.ExecuteCommand($"/bmrai followoutofcombat {(enable ? "on" : "off")}");

    public static void AiSetMaxDistanceTarget(float dist) => Chat.ExecuteCommand($"/bmrai maxdistancetarget {dist:0.##}");

    public static void AiSetPositional(string positional) => Chat.ExecuteCommand($"/bmrai positional {positional}");

    /// <summary>Force-disable / disable AI navigation (so we can hand movement to vnavmesh).</summary>
    public static void AiForbidMovement(bool forbid) => Chat.ExecuteCommand($"/bmrai forbidmovement {(forbid ? "on" : "off")}");

    public static void AiForbidActions(bool forbid) => Chat.ExecuteCommand($"/bmrai forbidactions {(forbid ? "on" : "off")}");
}
