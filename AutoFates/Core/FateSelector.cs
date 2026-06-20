using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace AutoFates.Core;

/// <summary>
/// Classifies and prioritizes the FATEs currently present in the player's zone.
///
/// Priority rules (per user spec):
///  - Ignore fates whose remaining time is below MinFateTimeSeconds.
///  - Ignore fates outside the level window (more than LevelsAbovePlayer above the player).
///  - Respect the enabled fate-type mask.
///  - When PrioritizeLowTimer is set, prefer fates that are lower on their timer (closest to
///    expiring but still above the minimum) over physically-closer fates. Otherwise prefer nearest.
/// </summary>
public static class FateSelector
{
    // FATE icon ids (stable client icon ids) used as a fallback classification signal.
    private static readonly HashSet<uint> BossIcons = new() { 60722, 60723 };
    private static readonly HashSet<uint> CollectIcons = new() { 60729, 60730 };
    private static readonly HashSet<uint> DefendIcons = new() { 60727, 60728 };
    private static readonly HashSet<uint> EscortIcons = new() { 60725, 60726 };

    /// <summary>
    /// Classify a FATE's type. Primary signal is the Fate excel sheet's Rule / item fields
    /// (authoritative), with the client icon id as a fallback.
    ///
    /// Fate.Rule values (observed): 1=Battle, 2/3=Collect(hand-in), 4=Boss/Notorious,
    /// 5=Defend, 6=Escort. We treat the EventItem/TurnInEventItem presence as the strongest
    /// collect-fate indicator since those reference the gatherable hand-in item.
    /// </summary>
    public static FateType Classify(IFate fate)
    {
        try
        {
            var data = fate.GameData;
            if (data.ValueNullable is { } row)
            {
                // Collect fates expose a turn-in / event item.
                if (row.TurnInEventItem.RowId != 0 || row.EventItem.RowId != 0 || row.ReqEventItem.RowId != 0)
                    return FateType.Collect;

                switch (row.Rule)
                {
                    case 4: return FateType.Boss;
                    case 5: return FateType.Defend;
                    case 6: return FateType.Escort;
                }
            }
        }
        catch { /* fall through to icon heuristics */ }

        var icon = fate.MapIconId != 0 ? fate.MapIconId : fate.IconId;
        if (BossIcons.Contains(icon)) return FateType.Boss;
        if (CollectIcons.Contains(icon)) return FateType.Collect;
        if (DefendIcons.Contains(icon)) return FateType.Defend;
        if (EscortIcons.Contains(icon)) return FateType.Escort;

        try { if (fate.HandInCount > 0) return FateType.Collect; }
        catch { /* HandInCount may throw in some states */ }

        return FateType.Battle;
    }

    /// <summary>The hand-in item id for a collect FATE (0 if not a collect fate).</summary>
    public static uint GetCollectItemId(IFate fate)
    {
        try
        {
            var data = fate.GameData;
            if (data.ValueNullable is not { } row) return 0;
            if (row.TurnInEventItem.RowId != 0) return row.TurnInEventItem.RowId;
            if (row.EventItem.RowId != 0) return row.EventItem.RowId;
            if (row.ReqEventItem.RowId != 0) return row.ReqEventItem.RowId;
        }
        catch { /* ignore */ }
        return 0;
    }

    public readonly record struct Candidate(IFate Fate, FateType Type, float Distance, long TimeRemaining);

    /// <summary>Returns the list of valid candidate fates in the current zone, already filtered.</summary>
    public static List<Candidate> GetCandidates(Configuration c)
    {
        var list = new List<Candidate>();
        var me = Player.Object;
        if (me == null) return list;
        var myPos = me.Position;
        var myLevel = Player.Level;

        foreach (var fate in Svc.Fates)
        {
            if (fate == null) continue;
            if (fate.State != FateState.Running && fate.State != FateState.Preparing) continue;

            var time = fate.TimeRemaining;
            if (time > 0 && time < c.MinFateTimeSeconds) continue;

            // Level window: skip fates more than N levels above the player.
            if (fate.Level > myLevel + c.LevelsAbovePlayer) continue;

            var type = Classify(fate);
            if ((c.EnabledFateTypes & type) == 0) continue;

            var dist = Vector3.Distance(myPos, fate.Position);
            list.Add(new Candidate(fate, type, dist, time));
        }

        return list;
    }

    /// <summary>Picks the best fate to run next, or null if none qualify.</summary>
    public static Candidate? PickBest(Configuration c)
    {
        var candidates = GetCandidates(c);
        if (candidates.Count == 0) return null;

        if (c.PrioritizeLowTimer)
        {
            // Lowest remaining time first; fates with 0/unknown time (e.g. Preparation) go last.
            return candidates
                .OrderBy(x => x.TimeRemaining <= 0 ? long.MaxValue : x.TimeRemaining)
                .ThenBy(x => x.Distance)
                .First();
        }

        return candidates.OrderBy(x => x.Distance).First();
    }

    /// <summary>Find the fate the player is currently standing inside (within its radius), if any.</summary>
    public static IFate? GetCurrentFate()
    {
        var me = Player.Object;
        if (me == null) return null;
        var pos = me.Position;
        foreach (var fate in Svc.Fates)
        {
            if (fate == null) continue;
            if (fate.State != FateState.Running) continue;
            if (Vector3.Distance(pos, fate.Position) <= fate.Radius)
                return fate;
        }
        return null;
    }

    public static IFate? GetFateById(ushort fateId)
        => Svc.Fates.FirstOrDefault(f => f != null && f.FateId == fateId);
}
