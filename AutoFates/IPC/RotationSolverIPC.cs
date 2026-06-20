using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Reflection;

namespace AutoFates.IPC;

/// <summary>
/// Wrapper over RotationSolver Reborn. RSR exposes a text-command interface (/rotation, /rsr)
/// for the common state changes we need. It does not navigate; pair with vnavmesh for movement.
/// </summary>
public static class RotationSolverIPC
{
    public const string InternalName = "RotationSolver";

    public static bool IsInstalled => DalamudReflector.TryGetDalamudPlugin(InternalName, out _, true, true);

    /// <summary>Auto mode: RSR will pick targets and attack on its own (good for fate farming).</summary>
    public static void SetAuto() => Chat.ExecuteCommand("/rotation auto");

    public static void SetManual() => Chat.ExecuteCommand("/rotation manual");

    public static void SetOff() => Chat.ExecuteCommand("/rotation off");

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
