using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Reflection;

namespace Autofate.IPC;

/// <summary>
/// Wrapper over RotationSolver Reborn. RSR exposes a text-command interface (/rotation, /rsr)
/// for the common state changes we need. It does not navigate; pair with vnavmesh for movement.
/// </summary>
public static class RotationSolverIPC
{
    public const string InternalName = "RotationSolver";

    public static bool IsInstalled => DalamudReflector.TryGetDalamudPlugin(InternalName, out _, true, true);

    private enum Mode { Unknown, Auto, Manual, Off }

    // CHANGE-GUARD: RSR's state changes are CHAT COMMANDS ("/rotation auto|manual|off"). Our combat
    // controller calls StartCombat/StopCombat every tick (fate arrival, clearing-aggro engage, etc.)
    // and the state machine can bounce between engage/idle rapidly — re-firing the same chat command
    // each tick made RSR "infinitely flick between off and on" mid-combat (each command re-triggers
    // RSR's mode and spams chat). So we remember the last mode we issued and only send the command
    // on an ACTUAL transition. (Same approach as BossModIPC.AiEnable.)
    private static Mode _lastMode = Mode.Unknown;

    /// <summary>Auto mode: RSR will pick targets and attack on its own (good for fate farming).</summary>
    public static void SetAuto() => Set(Mode.Auto, "/rotation auto");

    public static void SetManual() => Set(Mode.Manual, "/rotation manual");

    public static void SetOff() => Set(Mode.Off, "/rotation off");

    private static void Set(Mode mode, string command)
    {
        if (_lastMode == mode) return; // no change -> don't re-issue (prevents the off/on flicker)
        _lastMode = mode;
        Chat.ExecuteCommand(command);
    }

    /// <summary>Reset the cached mode (call on hard Stop so the next run re-issues the command).</summary>
    public static void ResetModeCache() => _lastMode = Mode.Unknown;

    /// <summary>Adds an enemy by Name/ID to RSR's priority target list (used to focus fate mobs).</summary>
    public static void AddPriorityTarget(uint nameId)
    {
        try { Svc.PluginInterface.GetIpcSubscriber<uint, object>("RotationSolverReborn.AddPriorityNameID").InvokeAction(nameId); }
        catch (Exception e) { Svc.Log.Verbose($"[RSR] AddPriorityNameID failed: {e.Message}"); }
    }

    public static void RemovePriorityTarget(uint nameId)
    {
        try { Svc.PluginInterface.GetIpcSubscriber<uint, object>("RotationSolverReborn.RemovePriorityNameID").InvokeAction(nameId); }
        catch (Exception e) { Svc.Log.Verbose($"[RSR] RemovePriorityNameID failed: {e.Message}"); }
    }
}
