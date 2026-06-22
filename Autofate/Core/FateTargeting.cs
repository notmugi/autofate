using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Autofate.Core;

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
    public static IGameObject? FindFateStartNpc(ushort fateId) => FindFateStartNpc(fateId, 0f);

    /// <summary>
    /// Find the fate-start "!" NPC. Many escort/defend start NPCs have FateId=0 UNTIL you talk to
    /// them, so a strict FateId match misses them. We first try an exact FateId match, then fall
    /// back to any targetable non-enemy NPC showing a nameplate icon within <paramref name="fateRadius"/>
    /// of the fate center (the "!" giver standing in the ring).
    /// </summary>
    public static IGameObject? FindFateStartNpc(ushort fateId, float fateRadius)
    {
        if (fateId == 0) return null;
        var me = Player.Object;
        if (me == null) return null;

        IGameObject? exact = null, nearby = null;
        var exactSq = float.MaxValue;
        var nearbySq = float.MaxValue;

        // Fate center for the radius fallback.
        Vector3 center = me.Position;
        var radSq = fateRadius > 0 ? fateRadius * fateRadius : float.MaxValue;
        foreach (var f in Svc.Fates)
            if (f != null && f.FateId == fateId) { center = f.Position; break; }

        foreach (var obj in Svc.Objects)
        {
            if (!obj.IsTargetable) continue;
            if (obj is IBattleNpc bnpc && IsAttackableEnemy(bnpc)) continue;
            if (GetNameplateIcon(obj) == 0) continue; // must show the "!"

            var d = Vector3.DistanceSquared(me.Position, obj.Position);
            if (GetFateId(obj) == fateId)
            {
                if (d < exactSq) { exactSq = d; exact = obj; }
            }
            else if (fateRadius > 0 && Vector3.DistanceSquared(center, obj.Position) <= radSq)
            {
                // FateId not set yet (un-started escort/defend NPC) but it's an "!" giver in our ring.
                if (d < nearbySq) { nearbySq = d; nearby = obj; }
            }
        }
        return exact ?? nearby;
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

    /// <summary>
    /// True if this fate enemy is part of our "pile". We count it as pulled if it is directly
    /// targeting us / our chocobo, OR — to align with BMR's broader <c>AggroPlayer</c> semantics so
    /// our nav-driven pull cap and BMR's MaxTargets agree at the boundary — if it's flagged in
    /// combat and within a short radius of us (it has clearly been pulled even if it momentarily
    /// retargets the chocobo or another player). The radius keeps distant in-combat mobs (engaged by
    /// other players) out of our count.
    /// </summary>
    public static bool IsAggroedOnUs(IBattleNpc bnpc, ulong myId, ulong chocoId)
    {
        if (bnpc.TargetObjectId == myId || (chocoId != 0 && bnpc.TargetObjectId == chocoId))
            return true;
        // Tolerance: in-combat fate mob hugging the pile counts as pulled.
        var me = Player.Object;
        if (me == null) return false;
        var inCombat = (bnpc.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.InCombat) != 0;
        if (!inCombat) return false;
        const float pileRadius = 8f;
        return Vector3.DistanceSquared(me.Position, bnpc.Position) <= pileRadius * pileRadius;
    }

    /// <summary>
    /// Count fate enemies currently aggroed onto us/our chocobo (the real "pile" size for mass
    /// pulling, AutoDuty-style). No range limit — counts every aggroed fate enemy.
    /// </summary>
    public static int CountAggroedFateEnemies(ushort fateId)
    {
        var me = Player.Object;
        if (me == null) return 0;
        var myId = me.GameObjectId;
        var chocoId = GetChocoboId();
        var n = 0;
        foreach (var e in GetFateEnemies(fateId))
        {
            if (IsAggroedOnUs(e, myId, chocoId)) n++;
        }
        return n;
    }

    /// <summary>
    /// Mass-pull picker (AutoDuty KillInRange style): the nearest fate enemy that is NOT yet aggroed
    /// onto us. We path to it to body-pull its aggro, then move to the next. No range limit —
    /// returns null only when every fate enemy is already on us. Nearest-first.
    /// </summary>
    public static IBattleNpc? GetNearestUnaggroedFateEnemy(ushort fateId)
    {
        var me = Player.Object;
        if (me == null) return null;
        var myId = me.GameObjectId;
        var chocoId = GetChocoboId();
        foreach (var e in GetFateEnemies(fateId)) // nearest-first
        {
            if (IsAggroedOnUs(e, myId, chocoId)) continue; // already pulled
            return e;
        }
        return null;
    }

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
    /// The turn-in NPC for a collect fate. Resolves AUTHORITATIVELY from the live FateContext's
    /// ObjectiveNpc entity id (the game tells us exactly which object is the hand-in NPC), the same
    /// approach BMR/DWD use, rather than guessing via the nameplate "!" heuristic (which is flaky on
    /// collect fates because the giver and the turn-in NPC can differ). Falls back to the "!"
    /// heuristic only if the objective id isn't available yet.
    /// </summary>
    public static IGameObject? GetCollectTurnInNpc(ushort fateId)
    {
        var objId = GetFateObjectiveNpcId(fateId);
        if (objId != 0)
        {
            var npc = Svc.Objects.FirstOrDefault(o => o.GameObjectId == objId);
            if (npc != null) return npc;
        }
        return FindFateStartNpc(fateId);
    }

    /// <summary>
    /// Read the ObjectiveNpc entity id off the live FateContext for the given fate (0 if not the
    /// current fate or unavailable). This is the hand-in / objective NPC the game itself designates.
    /// </summary>
    public static ulong GetFateObjectiveNpcId(ushort fateId)
    {
        if (fateId == 0) return 0;
        try
        {
            var fm = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager.Instance();
            if (fm == null) return 0;
            var cur = fm->CurrentFate;
            if (cur == null || cur->FateId != fateId) return 0;
            return cur->ObjectiveNpc;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Read the game-designated MotivationNpc entity id off the live FateContext (0 if not the
    /// current fate, unavailable, or unset). This is THE NPC the game ties the fate to — for escort
    /// / follow fates it's the NPC you escort. This is exactly what the reference plugins key off
    /// (their fate.MotivationNpc), NOT "any friendly NPC in the ring". Empty is 0xE0000000.
    /// </summary>
    public static ulong GetFateMotivationNpcId(ushort fateId)
    {
        if (fateId == 0) return 0;
        try
        {
            var fm = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager.Instance();
            if (fm == null) return 0;
            var cur = fm->CurrentFate;
            if (cur == null || cur->FateId != fateId) return 0;
            ulong id = cur->MotivationNpc;
            return id == 0xE0000000 ? 0 : id; // 0xE0000000 = "no NPC"
        }
        catch { return 0; }
    }

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

    /// <summary>
    /// The fate id the game says we are CURRENTLY registered to (joined), or 0 if none. This is the
    /// authoritative "what fate am I actually in" signal — the same thing BMR keys its targeting off
    /// — and is distinct from a fate whose ring we merely pass through. When escorting an NPC that
    /// walks through another fate, our joined fate stays the escort; the pass-through fate's mobs
    /// carry a DIFFERENT FateId, so anything scoped to this id ignores them.
    /// </summary>
    public static ushort GetJoinedFateId()
    {
        try
        {
            var fm = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager.Instance();
            if (fm == null) return 0;
            var cur = fm->CurrentFate;
            return cur == null ? (ushort)0 : cur->FateId;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Enemies attacking us (or our chocobo) that ALSO belong to the given fate. Nearest-first. Use
    /// this instead of <see cref="GetEnemiesAttackingMe"/> while running a specific fate so that a
    /// pass-through fate's mob landing a hit on us can't drag us into fighting its fate (the escort
    /// cross-fate bug). FateId is authoritative: foreign-fate mobs are never included.
    /// </summary>
    public static List<IBattleNpc> GetFateEnemiesAttackingMe(ushort fateId)
    {
        var me = Player.Object;
        var result = new List<IBattleNpc>();
        if (me == null || fateId == 0) return result;
        var myId = me.GameObjectId;
        var chocoId = GetChocoboId();
        foreach (var obj in Svc.Objects)
        {
            if (obj is not IBattleNpc bnpc) continue;
            if (!IsFateEnemy(bnpc, fateId)) continue; // FateId-scoped + attackable
            if (bnpc.TargetObjectId != myId && (chocoId == 0 || bnpc.TargetObjectId != chocoId)) continue;
            result.Add(bnpc);
        }
        var mp = me.Position;
        result.Sort((a, b) => Vector3.DistanceSquared(a.Position, mp).CompareTo(Vector3.DistanceSquared(b.Position, mp)));
        return result;
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
