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
    public static bool MoveTo(Configuration c, Vector3 dest, float stopRange = 3f)
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

        var fly = MountManager.ShouldFly(c);

        // Mount up for long trips if configured.
        if (c.UseMount && !MountManager.IsMounted && dist > c.MountDistanceThreshold && MountManager.CanMountHere)
        {
            MountManager.Mount(c);
            // While the mount animation plays, hold off on pathing this frame.
            return false;
        }

        // If mounted and flight desired, make sure we're airborne.
        if (fly && MountManager.IsMounted)
            MountManager.EnsureAirborne(c);

        // Hand the destination to vnavmesh if it isn't already pathing there.
        if (!NavmeshIPC.IsRunning() && !NavmeshIPC.PathfindInProgress())
        {
            if (!NavmeshIPC.IsReady())
            {
                if (ECommons.Throttlers.EzThrottler.Throttle("AF_NavNotReady", 5000))
                    Svc.Log.Debug("[Navigator] vnavmesh not ready (mesh still loading).");
                return false;
            }
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
