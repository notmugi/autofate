using System.Numerics;
using AutoFates.Features;
using AutoFates.IPC;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace AutoFates.Core;

/// <summary>
/// Movement abstraction over vnavmesh that automatically mounts and (where allowed) flies for
/// long-distance travel. The controller calls <see cref="MoveTo"/> each tick toward a target; the
/// navigator decides whether to mount, take off, and how to feed the destination to vnavmesh.
/// </summary>
public static class Navigator
{
    private static Vector3 _currentDest;
    private static bool _active;

    /// <summary>Distance to the current destination, or float.MaxValue if not navigating.</summary>
    public static float DistanceToDest()
    {
        var me = Player.Object;
        if (me == null || !_active) return float.MaxValue;
        return Vector3.Distance(me.Position, _currentDest);
    }

    public static bool IsNavigating => _active && NavmeshIPC.IsRunning();

    /// <summary>
    /// Drive movement toward <paramref name="dest"/>, stopping within <paramref name="stopRange"/>.
    /// Handles mounting/flying. Returns true once we are within range of the destination.
    /// Call repeatedly (idempotent / throttled internally by vnavmesh).
    /// </summary>
    public static bool MoveTo(Configuration c, Vector3 dest, float stopRange = 3f, bool allowMount = true)
    {
        var me = Player.Object;
        if (me == null) return false;

        _currentDest = dest;
        _active = true;

        var dist = Vector3.Distance(me.Position, dest);
        if (dist <= stopRange)
        {
            Stop();
            return true;
        }

        // CRITICAL: never issue movement until the navmesh for this zone has finished building.
        // Otherwise vnavmesh can't path and the engine skips ahead (e.g. teleporting onward).
        if (!NavmeshIPC.MeshReady())
        {
            if (ECommons.Throttlers.EzThrottler.Throttle("AF_NavBuilding", 3000))
            {
                var p = NavmeshIPC.BuildProgress();
                Svc.Log.Debug(p is >= 0 and < 1
                    ? $"[Navigator] Waiting for navmesh build: {p * 100:0}%"
                    : "[Navigator] Waiting for navmesh to be ready...");
            }
            return false;
        }

        var fly = MountManager.ShouldFly(c);

        // Mount up for long trips if configured. Short in-fate repositioning passes allowMount=false
        // so we never re-mount for tiny hops (which previously caused a mount/dismount loop).
        if (allowMount && c.UseMount && !MountManager.IsMounted && dist > c.MountDistanceThreshold && MountManager.CanMountHere)
        {
            MountManager.Mount(c);
            // While the mount animation plays, hold off on pathing this frame.
            return false;
        }

        // Hand the destination to vnavmesh if it isn't already pathing there.
        // We pass fly=true and let vnavmesh perform the takeoff itself — we do NOT manually jump
        // here (that caused the endless-jumping bug by fighting vnavmesh's own takeoff).
        if (!NavmeshIPC.IsRunning() && !NavmeshIPC.PathfindInProgress())
        {
            NavmeshIPC.PathfindAndMoveCloseTo(dest, stopRange, fly && MountManager.IsMounted);
        }

        return false;
    }

    public static void Stop()
    {
        if (_active)
        {
            NavmeshIPC.Stop();
            _active = false;
        }
    }
}
