using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AutoFates.Core;

/// <summary>
/// FATE mob targeting. Identifies enemies that belong to a specific FATE via the game object's
/// FateId field (UInt16 on FFXIVClientStructs GameObject), so we only ever engage mobs that count
/// toward the FATE and never aggro unrelated overworld enemies.
/// </summary>
public static unsafe class FateTargeting
{
    /// <summary>Read the FateId off a Dalamud game object (0 = not part of any fate).</summary>
    public static ushort GetFateId(IGameObject obj)
    {
        if (obj.Address == nint.Zero) return 0;
        return ((CSGameObject*)obj.Address)->FateId;
    }

    /// <summary>Read the nameplate icon id (0 = none). The fate-start NPC carries the FATE "!" icon.</summary>
    public static uint GetNameplateIcon(IGameObject obj)
    {
        if (obj.Address == nint.Zero) return 0;
        return ((CSGameObject*)obj.Address)->NamePlateIconId;
    }

    /// <summary>
    /// Find the fate-START NPC for a fate (the one you interact with to begin a defend/escort fate
    /// — pops Talk dialogue then a Yes/No). Heuristic: an interactable, NON-enemy object carrying
    /// this FateId with a nameplate icon (the orange "!"). Returns the nearest match, or null.
    /// </summary>
    public static IGameObject? FindFateStartNpc(ushort fateId)
    {
        if (fateId == 0) return null;
        var me = Player.Object;
        if (me == null) return null;
        IGameObject? best = null;
        var bestSq = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (!obj.IsTargetable) continue;
            if (GetFateId(obj) != fateId) continue;
            // Must NOT be an attackable enemy, and must show a nameplate icon (the fate "!").
            if (obj is IBattleNpc bnpc && IsAttackableEnemy(bnpc)) continue;
            if (GetNameplateIcon(obj) == 0) continue;
            var d = Vector3.DistanceSquared(me.Position, obj.Position);
            if (d < bestSq) { bestSq = d; best = obj; }
        }
        return best;
    }

    /// <summary>
    /// True if this object is a live enemy belonging to the given fate. We match on the FateId
    /// field directly: only mobs that count toward this fate carry its id, so this never selects
    /// unrelated overworld enemies. (No BattleNpcKind check needed — FateId is authoritative.)
    /// </summary>
    public static bool IsFateEnemy(IGameObject obj, ushort fateId)
    {
        if (fateId == 0) return false;
        if (obj is not IBattleNpc bnpc) return false;
        if (GetFateId(obj) != fateId) return false;
        return IsAttackableEnemy(bnpc);
    }

    /// <summary>
    /// Strict hostile check using the NAMEPLATE COLOR — the same authoritative method ECommons and
    /// AutoDuty use. This is the only reliable way to tell an attackable enemy from a friendly NPC,
    /// an objective object (e.g. a "Fish Basket"), or an escort/guard NPC. BattleNpcSubKind is NOT
    /// reliable. Hostile nameplate kinds: 7 (yellow attackable), 9 (red engaged), 10/11 (engaged/
    /// aggroed), 4/5/6 (PvP). Friendlies (green/other) are excluded.
    /// </summary>
    public static bool IsAttackableEnemy(IBattleNpc bnpc)
    {
        if (bnpc.IsDead || bnpc.CurrentHp == 0) return false;
        if (!bnpc.IsTargetable) return false;
        try
        {
            return ECommons.GameFunctions.ObjectFunctions.IsHostile(bnpc);
        }
        catch
        {
            // Fallback if the nameplate sig ever breaks: require the hostile Combatant subkind.
            return bnpc.BattleNpcKind == Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant;
        }
    }

    /// <summary>All live enemies belonging to the fate, ordered nearest-first.</summary>
    public static List<IBattleNpc> GetFateEnemies(ushort fateId)
    {
        var me = Player.Object;
        var result = new List<IBattleNpc>();
        if (me == null) return result;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc bnpc) continue;
            if (!IsFateEnemy(bnpc, fateId)) continue;
            result.Add(bnpc);
        }
        var mp = me.Position;
        result.Sort((a, b) => Vector3.DistanceSquared(a.Position, mp).CompareTo(Vector3.DistanceSquared(b.Position, mp)));
        return result;
    }

    /// <summary>Nearest live enemy belonging to the fate, or null.</summary>
    public static IBattleNpc? GetNearestFateEnemy(ushort fateId)
    {
        var list = GetFateEnemies(fateId);
        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>
    /// Friendly NPCs that belong to the fate and have a health bar — i.e. the protect targets in a
    /// Defend fate (and escort NPCs). These carry the FateId but are NOT the Combatant subkind.
    /// </summary>
    public static List<IBattleNpc> GetDefendedFriendlies(ushort fateId)
    {
        var result = new List<IBattleNpc>();
        if (fateId == 0) return result;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc bnpc) continue;
            if (bnpc.IsDead) continue;
            if (GetFateId(bnpc) != fateId) continue;
            // Friendly = NOT hostile (by nameplate), but has a health bar (MaxHp > 0).
            if (IsAttackableEnemy(bnpc)) continue;
            if (bnpc.MaxHp == 0) continue;
            result.Add(bnpc);
        }
        return result;
    }

    /// <summary>
    /// If any fate friendly (NPC with a health bar) is currently under attack, return the nearest
    /// enemy attacking a friendly — i.e. the threat we must peel. Null if no friendly is being
    /// attacked. Works for ANY fate (not just ones classified as Defend), since many fates have
    /// guard/escort NPCs that aren't tagged as Defend.
    /// </summary>
    public static IBattleNpc? GetActiveDefendThreat(ushort fateId)
    {
        var friendlies = GetDefendedFriendlies(fateId);
        if (friendlies.Count == 0) return null;

        var friendlyIds = new HashSet<ulong>();
        foreach (var f in friendlies) friendlyIds.Add(f.GameObjectId);

        // enemies is nearest-first; first one targeting a friendly is the closest active threat.
        foreach (var e in GetFateEnemies(fateId))
            if (e.TargetObjectId != 0 && friendlyIds.Contains(e.TargetObjectId))
                return e;
        return null;
    }

    /// <summary>
    /// Defend-fate targeting: pick the enemy that is attacking one of the protected friendlies,
    /// nearest such enemy first. Falls back to the nearest fate enemy if none are currently
    /// targeting a friendly. Returns null if there are no fate enemies at all.
    /// </summary>
    public static IBattleNpc? GetDefendTarget(ushort fateId)
    {
        var enemies = GetFateEnemies(fateId);
        if (enemies.Count == 0) return null;

        var friendlies = GetDefendedFriendlies(fateId);
        if (friendlies.Count == 0) return enemies[0]; // nothing to defend yet -> nearest enemy

        var friendlyIds = new HashSet<ulong>();
        foreach (var f in friendlies) friendlyIds.Add(f.GameObjectId);

        // Enemies already locked onto a protected friendly (these are the real threat). enemies is
        // already nearest-first, so the first match is the closest threat.
        foreach (var e in enemies)
            if (e.TargetObjectId != 0 && friendlyIds.Contains(e.TargetObjectId))
                return e;

        // No enemy targeting a friendly right now -> just clear the nearest enemy.
        return enemies[0];
    }

    public static int CountFateEnemies(ushort fateId) => GetFateEnemies(fateId).Count;

    /// <summary>Count live fate enemies within <paramref name="range"/> yalms of the player.</summary>
    public static int CountFateEnemiesWithin(ushort fateId, float range)
    {
        var me = Player.Object;
        if (me == null) return 0;
        var n = 0;
        var rSq = range * range;
        foreach (var e in GetFateEnemies(fateId))
            if (Vector3.DistanceSquared(me.Position, e.Position) <= rSq) n++;
        return n;
    }

    /// <summary>
    /// Mass-pull target picker: the nearest fate enemy that is NOT already gathered up on us (i.e.
    /// further than <paramref name="gatheredRange"/>) but still within <paramref name="leashRange"/>
    /// so we body-pull nearby stragglers into the pile WITHOUT sprinting across the whole fate.
    /// Returns null if there's nothing to pull within the leash (or no enemies at all).
    /// </summary>
    public static IBattleNpc? GetNearestUngatheredEnemy(ushort fateId, float gatheredRange, float leashRange)
    {
        var me = Player.Object;
        if (me == null) return null;
        var gSq = gatheredRange * gatheredRange;
        var lSq = leashRange * leashRange;
        foreach (var e in GetFateEnemies(fateId)) // nearest-first
        {
            var d = Vector3.DistanceSquared(me.Position, e.Position);
            if (d <= gSq) continue;       // already gathered on us
            if (d > lSq) return null;      // nearest ungathered is beyond leash -> don't chase
            return e;
        }
        return null;
    }

    /// <summary>True if this fate enemy is aggroed onto us (or our chocobo) — i.e. in combat and
    /// targeting us. These are the mobs already "pulled" onto our pile.</summary>
    public static bool IsAggroedOnUs(IBattleNpc bnpc, ulong myId, ulong chocoId)
        => bnpc.TargetObjectId == myId || (chocoId != 0 && bnpc.TargetObjectId == chocoId);

    /// <summary>
    /// Count fate enemies currently aggroed onto us/our chocobo within <paramref name="range"/>.
    /// This is the real "pile" size for mass pulling (AutoDuty-style), unlike proximity counting.
    /// </summary>
    public static int CountAggroedFateEnemies(ushort fateId, float range)
    {
        var me = Player.Object;
        if (me == null) return 0;
        var myId = me.GameObjectId;
        var chocoId = GetChocoboId();
        var rSq = range * range;
        var n = 0;
        foreach (var e in GetFateEnemies(fateId))
        {
            if (Vector3.DistanceSquared(me.Position, e.Position) > rSq) continue;
            if (IsAggroedOnUs(e, myId, chocoId)) n++;
        }
        return n;
    }

    /// <summary>
    /// Mass-pull picker (AutoDuty KillInRange style): the nearest fate enemy within
    /// <paramref name="pullRange"/> that is NOT yet aggroed onto us. We path to it to body-pull its
    /// aggro, then move to the next. Returns null when every enemy in range is already on us.
    /// </summary>
    public static IBattleNpc? GetNearestUnaggroedFateEnemy(ushort fateId, float pullRange)
    {
        var me = Player.Object;
        if (me == null) return null;
        var myId = me.GameObjectId;
        var chocoId = GetChocoboId();
        var rSq = pullRange * pullRange;
        foreach (var e in GetFateEnemies(fateId)) // nearest-first
        {
            if (Vector3.DistanceSquared(me.Position, e.Position) > rSq) continue;
            if (IsAggroedOnUs(e, myId, chocoId)) continue; // already pulled
            return e;
        }
        return null;
    }

    /// <summary>
    /// Enemies that are currently attacking us (in combat, targeting the player), regardless of
    /// whether they belong to a fate. Used to clean up accidental pulls before moving on so we're
    /// not stuck being beaten on by a stray mob. Nearest-first.
    /// </summary>
    /// <summary>
    /// Ground collectables for a collect fate (e.g. "Fallen Lumber"): interactable EventObj objects
    /// carrying this FateId. Nearest-first.
    /// </summary>
    public static IGameObject? GetNearestCollectable(ushort fateId)
    {
        if (fateId == 0) return null;
        var me = Player.Object;
        if (me == null) return null;
        IGameObject? best = null;
        var bestSq = float.MaxValue;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj) continue;
            if (!obj.IsTargetable) continue;
            if (GetFateId(obj) != fateId) continue;
            var d = Vector3.DistanceSquared(me.Position, obj.Position);
            if (d < bestSq) { bestSq = d; best = obj; }
        }
        return best;
    }

    /// <summary>
    /// The turn-in NPC for a collect fate: a non-enemy object with this FateId carrying a nameplate
    /// icon. (Same detection as the start NPC, but used for collect hand-ins.)
    /// </summary>
    public static IGameObject? GetCollectTurnInNpc(ushort fateId) => FindFateStartNpc(fateId);

    /// <summary>Our chocobo companion's object id (object whose OwnerId == our id), or 0.</summary>
    public static ulong GetChocoboId()
    {
        var me = Player.Object;
        if (me == null) return 0;
        var myId = me.GameObjectId;
        foreach (var obj in Svc.Objects)
        {
            if (obj.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc) continue;
            if (obj.OwnerId == myId) return obj.GameObjectId;
        }
        return 0;
    }

    public static List<IBattleNpc> GetEnemiesAttackingMe()
    {
        var me = Player.Object;
        var result = new List<IBattleNpc>();
        if (me == null) return result;
        var myId = me.GameObjectId;
        var chocoId = GetChocoboId();
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc bnpc) continue;
            if (!IsAttackableEnemy(bnpc)) continue;
            // Hostile targeting us OR our chocobo.
            if (bnpc.TargetObjectId != myId && (chocoId == 0 || bnpc.TargetObjectId != chocoId)) continue;
            result.Add(bnpc);
        }
        var mp = me.Position;
        result.Sort((a, b) => Vector3.DistanceSquared(a.Position, mp).CompareTo(Vector3.DistanceSquared(b.Position, mp)));
        return result;
    }

    /// <summary>
    /// Nearest attackable hostile within range, regardless of who it's targeting. Used to clear
    /// stray aggro after a fate (the mob may be hitting our chocobo, not us, so "attacking me"
    /// detection misses it). Nearest-first.
    /// </summary>
    public static IBattleNpc? GetNearestHostile(float maxRange = 40f)
    {
        var me = Player.Object;
        if (me == null) return null;
        IBattleNpc? best = null;
        var bestSq = maxRange * maxRange;
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc bnpc) continue;
            if (!IsAttackableEnemy(bnpc)) continue;
            var d = Vector3.DistanceSquared(me.Position, bnpc.Position);
            if (d <= bestSq) { bestSq = d; best = bnpc; }
        }
        return best;
    }

    /// <summary>
    /// Force-open combat on the given target by firing the basic "Attack" action (Action #7). None
    /// of the rotation backends reliably OPEN combat on their own (RSR/BMR only act once in combat;
    /// Wrath needs a working lease), so we kick it off ourselves. This starts auto-attack, which
    /// puts us in combat and lets every backend take over. No-ops if already attacking this target.
    /// </summary>
    public static void StartAutoAttack(IBattleNpc target)
    {
        if (target == null || target.IsDead || target.CurrentHp == 0) return;
        var am = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
        if (am == null) return;
        // Action 7 = "Attack" (toggles auto-attack on the current target).
        if (!ECommons.Throttlers.EzThrottler.Throttle("AF_AutoAttack", 500)) return;
        try
        {
            am->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.Action, 7, target.GameObjectId);
        }
        catch (System.Exception e) { Svc.Log.Verbose($"[Combat] StartAutoAttack failed: {e.Message}"); }
    }

    /// <summary>Ensure we're targeting the nearest enemy attacking us. Returns it, or null if none.</summary>
    public static IBattleNpc? EnsureAttackerTarget()
    {
        if (Svc.Targets.Target is IBattleNpc cur && !cur.IsDead && cur.CurrentHp > 0
            && cur is { } c && c.TargetObjectId == (Player.Object?.GameObjectId ?? 0))
            return cur;
        var list = GetEnemiesAttackingMe();
        if (list.Count == 0) return null;
        Svc.Targets.Target = list[0];
        return list[0];
    }

    /// <summary>
    /// Ensure we have a live FATE enemy targeted. Returns the target (existing or newly set), or
    /// null if there are no fate enemies in range. Only retargets when the current target isn't a
    /// valid fate enemy, so we don't yank the target away from the combat backend mid-cast.
    /// </summary>
    public static IBattleNpc? EnsureFateTarget(ushort fateId, bool defendPriority = false)
    {
        // For Defend fates, ALWAYS prefer the enemy threatening a protected friendly, even if our
        // current target is a valid enemy — the threat to the NPC takes priority.
        if (defendPriority)
        {
            var threat = GetDefendTarget(fateId);
            if (threat != null)
            {
                if (Svc.Targets.Target is not IBattleNpc curt || curt.GameObjectId != threat.GameObjectId)
                    Svc.Targets.Target = threat;
                return threat;
            }
        }

        // PRIORITY: if anything is actually attacking us (or our chocobo), deal with the nearest
        // attacker first — standing still pointing at a distant mob while others beat on us is the
        // bug we're fixing. Only switch if our current target ISN'T already one of the attackers.
        var attackers = GetEnemiesAttackingMe();
        if (attackers.Count > 0)
        {
            var curId = (Svc.Targets.Target as IBattleNpc)?.GameObjectId ?? 0;
            var alreadyOnAttacker = attackers.Exists(a => a.GameObjectId == curId);
            if (!alreadyOnAttacker)
            {
                // Prefer an attacker that belongs to this fate; otherwise the nearest attacker.
                var fa = attackers.Find(a => IsFateEnemy(a, fateId)) ?? attackers[0];
                Svc.Targets.Target = fa;
                return fa;
            }
            // Current target is already an attacker — keep it (don't yank mid-cast).
            return Svc.Targets.Target as IBattleNpc;
        }

        // Keep the current target if it's still a valid fate enemy.
        if (Svc.Targets.Target is IBattleNpc cur && IsFateEnemy(cur, fateId))
            return cur;

        var next = GetNearestFateEnemy(fateId);
        if (next != null)
            Svc.Targets.Target = next;
        return next;
    }
}
