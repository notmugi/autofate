using System.Numerics;
using Autofate.Features;
using Autofate.IPC;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace Autofate.Core;

/// <summary>
/// Movement over vnavmesh that auto-mounts and (where allowed) flies for long trips. Call
/// <see cref="MoveTo"/> each tick toward a target.
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

        // Never move until the zone navmesh has finished building, or vnavmesh can't path.
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

        // Mount for long trips if configured. allowMount=false for short hops to avoid a mount loop.
        if (allowMount && c.UseMount && !MountManager.IsMounted && dist > c.MountDistanceThreshold && MountManager.CanMountHere)
        {
            MountManager.Mount(c);
            // Hold off on pathing while the mount animation plays.
            return false;
        }

        // Sprint whenever travelling on foot; no-ops if already sprinting/mounted/occupied.
        if (!MountManager.IsMounted)
            MountManager.Sprint();

        // Hand the destination to vnavmesh; let it perform takeoff itself (don't manually jump).
        if (!NavmeshIPC.IsRunning() && !NavmeshIPC.PathfindInProgress())
        {
            NavmeshIPC.PathfindAndMoveCloseTo(dest, stopRange, fly && MountManager.IsMounted);
        }

        return false;
    }

    /// <summary>
    /// Follow a moving target (party leader): re-issues the path as the target drifts so we chase a
    /// walking player. No mounting / stop-on-arrival.
    /// </summary>
    public static void FollowMoveTo(Configuration c, Vector3 target, float followDistance)
    {
        var me = Player.Object;
        if (me == null) return;
        if (!NavmeshIPC.MeshReady()) return;

        _active = true;
        var dist = Vector3.Distance(me.Position, target);

        // Close enough: stop and don't jitter.
        if (dist <= Math.Max(1f, followDistance))
        {
            NavmeshIPC.Stop();
            return;
        }

        // Fly to keep pace with a flying leader; fly=true once mounted so vnav starts takeoff.
        var fly = c.UseFlight && MountManager.CanFlyHere && MountManager.IsMounted;

        if (!MountManager.IsMounted)
        {
            // Sprint on foot to keep up.
            MountManager.Sprint();
        }
        else if (fly && !MountManager.IsFlying)
        {
            // Mounted but grounded and we want to fly: take off immediately.
            MountManager.EnsureAirborne(c);
        }

        // Use direct Path.MoveTo (not async A* pathfind) so frequent re-issues don't cancel a
        // pending pathfind. Re-issue when not moving or the target drifts (throttled 100ms).
        var drift = Vector3.Distance(_currentDest, target);
        if (!NavmeshIPC.IsRunning() || drift > 1f)
        {
            if (ECommons.Throttlers.EzThrottler.Throttle("AF_FollowRepath", 100))
            {
                _currentDest = target;
                NavmeshIPC.MoveTo(new List<Vector3> { target }, fly);
            }
        }
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
