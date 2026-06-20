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

    /// <summary>
    /// True if this object is a live enemy belonging to the given fate. We match on the FateId
    /// field directly: only mobs that count toward this fate carry its id, so this never selects
    /// unrelated overworld enemies. (No BattleNpcKind check needed — FateId is authoritative.)
    /// </summary>
    public static bool IsFateEnemy(IGameObject obj, ushort fateId)
    {
        if (fateId == 0) return false;
        if (obj is not IBattleNpc bnpc) return false;
        if (bnpc.IsDead) return false;
        if (GetFateId(obj) != fateId) return false;
        // ONLY strictly-attackable enemies. Fates can contain friendly NPCs (escort targets,
        // turn-in NPCs, party-member allies) that also carry the FateId — never target those.
        // BattleNpcSubKind.Combatant (5) is the hostile-enemy subkind; friendly fate NPCs are
        // BNpcPart / NpcPartyMember / etc.
        if (bnpc.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant)
            return false;
        // Must be targetable & alive.
        return bnpc.IsTargetable && bnpc.CurrentHp > 0;
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

    public static int CountFateEnemies(ushort fateId) => GetFateEnemies(fateId).Count;

    /// <summary>
    /// Ensure we have a live FATE enemy targeted. Returns the target (existing or newly set), or
    /// null if there are no fate enemies in range. Only retargets when the current target isn't a
    /// valid fate enemy, so we don't yank the target away from the combat backend mid-cast.
    /// </summary>
    public static IBattleNpc? EnsureFateTarget(ushort fateId)
    {
        // Keep the current target if it's still a valid fate enemy.
        if (Svc.Targets.Target is IBattleNpc cur && IsFateEnemy(cur, fateId))
            return cur;

        var next = GetNearestFateEnemy(fateId);
        if (next != null)
            Svc.Targets.Target = next;
        return next;
    }
}
