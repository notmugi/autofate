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

    // ---- stuck detection ----
    private static Vector3 _lastStuckSamplePos;
    private static long _lastProgressMs;          // last time we moved meaningfully
    private static long _unstuckUntilMs;          // we're mid-unstuck until this time
    private const float StuckMoveThreshold = 2f;  // must move >2y to count as progress
    private const long StuckTimeoutMs = 5000;     // no progress for 5s -> we're stuck
    private const long UnstuckDurationMs = 6000;  // run the unstuck maneuver for 6s (fly up / hop)

    /// <summary>
    /// Detect being stuck (position hasn't changed >2y in 5s while actively pathing) and perform an
    /// unstuck maneuver: jump repeatedly on the ground, or fly straight up for ~3s when airborne,
    /// then resume. Returns true while an unstuck maneuver is in progress (caller should let it run
    /// rather than issuing a normal path this frame). Call every tick while navigating.
    /// </summary>
    private static bool HandleStuck(Configuration c)
    {
        var me = Player.Object;
        if (me == null) return false;
        var now = System.Environment.TickCount64;
        var pos = me.Position;

        // If we're currently running an unstuck maneuver, keep it going until the timer elapses.
        if (now < _unstuckUntilMs)
        {
            if (MountManager.IsFlying)
                MountManager.Ascend();   // fly upward to clear the obstacle
            else
                MountManager.Jump();     // hop over small geometry
            return true;
        }

        // Sample progress. If we've moved enough, reset the timer.
        if (Vector3.Distance(pos, _lastStuckSamplePos) > StuckMoveThreshold)
        {
            _lastStuckSamplePos = pos;
            _lastProgressMs = now;
            return false;
        }

        // No meaningful movement: have we been stuck long enough?
        if (now - _lastProgressMs > StuckTimeoutMs)
        {
            Svc.Log.Information($"[Navigator] Stuck for {StuckTimeoutMs}ms (flying={MountManager.IsFlying}); running unstuck maneuver + recalculating path.");
            _unstuckUntilMs = now + UnstuckDurationMs;
            _lastProgressMs = now;            // reset so we don't immediately retrigger
            _lastStuckSamplePos = pos;
            NavmeshIPC.Stop();                // drop the current (failed) path so we recalc next move
            _currentDest = new Vector3(float.MaxValue); // force a re-path on the next FollowMoveTo
            return true;
        }
        return false;
    }

    /// <summary>Reset stuck tracking (call when starting a fresh navigation).</summary>
    private static void ResetStuck()
    {
        var me = Player.Object;
        _lastStuckSamplePos = me?.Position ?? Vector3.Zero;
        _lastProgressMs = System.Environment.TickCount64;
        _unstuckUntilMs = 0;
    }

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

        // STUCK CHECK: if we haven't progressed in 5s, run the unstuck maneuver (fly up / hop) and
        // force a re-path instead of issuing the same blocked path again.
        if (HandleStuck(c)) return false;

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

        // STUCK CHECK: if the leader is moving but we haven't progressed in 5s (wall/geometry), run
        // the unstuck maneuver (fly up 6s / hop) before re-issuing the path.
        if (HandleStuck(c)) return;

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
            ResetStuck();
        }
    }
}
