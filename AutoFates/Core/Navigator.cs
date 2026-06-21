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

        // SPRINT: whenever we're travelling on foot (not mounted) and Sprint is off cooldown, pop it.
        // Cheap to call (no-ops if already sprinting / occupied / mounted) and speeds up every hop.
        if (!MountManager.IsMounted)
            MountManager.Sprint();

        // Hand the destination to vnavmesh if it isn't already pathing there.
        // We pass fly=true and let vnavmesh perform the takeoff itself — we do NOT manually jump
        // here (that caused the endless-jumping bug by fighting vnavmesh's own takeoff).
        if (!NavmeshIPC.IsRunning() && !NavmeshIPC.PathfindInProgress())
        {
            NavmeshIPC.PathfindAndMoveCloseTo(dest, stopRange, fly && MountManager.IsMounted);
        }

        return false;
    }

    /// <summary>
    /// Follow a MOVING target (party leader). Unlike MoveTo, this re-issues the path whenever the
    /// target drifts from where we last pathed to, so we actually chase a walking player instead of
    /// stopping at their old position (AutoDuty FollowHelper model). No mounting/stop-on-arrival.
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

        // Flight: if flight is enabled and flyable here, fly to keep pace with a flying leader. We
        // pass fly=true as soon as we're mounted (don't wait until already airborne) so vnav begins
        // its own takeoff immediately, and we ALSO kick EnsureAirborne to take off ASAP.
        var fly = c.UseFlight && MountManager.CanFlyHere && MountManager.IsMounted;

        if (!MountManager.IsMounted)
        {
            // Sprint on foot to keep up.
            MountManager.Sprint();
        }
        else if (fly && !MountManager.IsFlying)
        {
            // Mounted but grounded and we want to fly -> take off immediately.
            MountManager.EnsureAirborne(c);
        }

        // Use Path.MoveTo (direct waypoint move) — NOT SimpleMove.PathfindAndMoveTo. The latter runs
        // an async A* pathfind; because we re-issue frequently to chase a moving target, we kept
        // cancelling the pathfind before it finished computing, so it never started moving. AutoDuty
        // follows with Path.MoveTo, which starts moving immediately toward the point. Re-issue when
        // not moving OR the target drifts from our last destination (throttled 100ms like AutoDuty).
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
