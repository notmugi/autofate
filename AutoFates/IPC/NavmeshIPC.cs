using System.Numerics;
using System.Threading.Tasks;
using ECommons.DalamudServices;
using ECommons.Reflection;

namespace AutoFates.IPC;

/// <summary>Thin wrapper over vnavmesh IPC. All calls are guarded so they no-op if vnav is absent.</summary>
public static class NavmeshIPC
{
    public const string Name = "vnavmesh";

    public static bool IsInstalled => DalamudReflector.TryGetDalamudPlugin(Name, out _, true, true);

    public static bool IsReady()
        => Invoke<bool>("vnavmesh.Nav.IsReady");

    public static bool PathfindInProgress()
        => Invoke<bool>("vnavmesh.SimpleMove.PathfindInProgress") || Invoke<bool>("vnavmesh.Nav.PathfindInProgress");

    public static bool IsRunning()
        => Invoke<bool>("vnavmesh.Path.IsRunning");

    /// <summary>Pathfind to a destination and start walking/flying. Returns whether the request started.</summary>
    public static bool PathfindAndMoveTo(Vector3 dest, bool fly = false)
        => Invoke<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo", dest, fly);

    public static bool PathfindAndMoveCloseTo(Vector3 dest, float range, bool fly = false)
        => Invoke<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo", dest, fly, range);

    public static void Stop()
        => InvokeAction("vnavmesh.Path.Stop");

    public static void MoveTo(List<Vector3> waypoints, bool fly = false)
        => InvokeAction("vnavmesh.Path.MoveTo", waypoints, fly);

    public static Vector3? PointOnFloor(Vector3 p, bool allowUnlandable = true, float halfExtentXZ = 5f)
        => Invoke<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor", p, allowUnlandable, halfExtentXZ);

    public static Vector3? NearestPoint(Vector3 p, float halfExtentXZ = 5f, float halfExtentY = 5f)
        => Invoke<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPoint", p, halfExtentXZ, halfExtentY);

    public static Task<List<Vector3>>? Pathfind(Vector3 from, Vector3 to, bool fly = false)
        => Invoke<Vector3, Vector3, bool, Task<List<Vector3>>>("vnavmesh.Nav.Pathfind", from, to, fly);

    // -------------------------------------------------------------- helpers
    private static TRet? Invoke<TRet>(string ep)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<TRet>(ep).InvokeFunc(); }
        catch (Exception e) { Log(ep, e); return default; }
    }

    private static TRet? Invoke<T1, TRet>(string ep, T1 a1)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<T1, TRet>(ep).InvokeFunc(a1); }
        catch (Exception e) { Log(ep, e); return default; }
    }

    private static TRet? Invoke<T1, T2, TRet>(string ep, T1 a1, T2 a2)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<T1, T2, TRet>(ep).InvokeFunc(a1, a2); }
        catch (Exception e) { Log(ep, e); return default; }
    }

    private static TRet? Invoke<T1, T2, T3, TRet>(string ep, T1 a1, T2 a2, T3 a3)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<T1, T2, T3, TRet>(ep).InvokeFunc(a1, a2, a3); }
        catch (Exception e) { Log(ep, e); return default; }
    }

    private static void InvokeAction(string ep)
    {
        try { Svc.PluginInterface.GetIpcSubscriber<object>(ep).InvokeAction(); }
        catch (Exception e) { Log(ep, e); }
    }

    private static void InvokeAction<T1, T2>(string ep, T1 a1, T2 a2)
    {
        try { Svc.PluginInterface.GetIpcSubscriber<T1, T2, object>(ep).InvokeAction(a1, a2); }
        catch (Exception e) { Log(ep, e); }
    }

    private static void Log(string ep, Exception e)
    {
        if (Plugin.C?.VerboseLogging == true)
            Svc.Log.Verbose($"[NavmeshIPC] {ep} failed: {e.Message}");
    }
}
