using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using ECommons.DalamudServices;
using ECommons.GameHelpers;

namespace Autofate.Core;

/// <summary>
/// Classifies and prioritizes FATEs in the player's zone. Filters by MinFateTimeSeconds, level
/// window, blacklist, and enabled types; picks lowest-timer or nearest per PrioritizeLowTimer.
/// </summary>
public static class FateSelector
{
    // Client FATE icon ids used as a fallback classification signal.
    private static readonly HashSet<uint> BossIcons = new() { 60722, 60723 };
    private static readonly HashSet<uint> CollectIcons = new() { 60729, 60730 };
    private static readonly HashSet<uint> DefendIcons = new() { 60727, 60728 };
    private static readonly HashSet<uint> EscortIcons = new() { 60725, 60726 };

    // Fate ids already logged a classification diagnostic (one line per fate).
    private static readonly HashSet<uint> _diagLogged = new();
    private static int SafeHandIn(IFate fate) { try { return fate.HandInCount; } catch { return -1; } }

    /// <summary>Distinct FATE names seen this session (first-seen order), for the blacklist UI.</summary>
    public static readonly List<string> SeenFateNames = new();

    private static void NoteSeenFate(IFate fate)
    {
        var name = fate.Name.ToString();
        if (string.IsNullOrEmpty(name)) return;
        if (!SeenFateNames.Contains(name)) SeenFateNames.Add(name);
    }

    /// <summary>
    /// Classify a FATE's type from the Fate excel sheet's Rule/item fields, falling back to the
    /// client icon id. Rule: 1=Battle, 2/3=Collect, 4=Boss, 5=Defend, 6=Escort; an EventItem/
    /// TurnInEventItem is the strongest collect indicator.
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

    /// <summary>
    /// The hand-in item id for a collect FATE (0 if not a collect fate). The authoritative source —
    /// the one BMR/DWD key off — is the Fate sheet's <c>EventItem</c> row: that's the collectable
    /// you pick up off the ground AND hand in. We prefer it, falling back to TurnIn/ReqEventItem
    /// only if EventItem is somehow unset (older/edge fates), since those can resolve to a different
    /// (wrong) row on some fates.
    /// </summary>
    public static uint GetCollectItemId(IFate fate)
    {
        try
        {
            var data = fate.GameData;
            if (data.ValueNullable is not { } row) return 0;
            if (row.EventItem.RowId != 0) return row.EventItem.RowId;       // authoritative
            if (row.TurnInEventItem.RowId != 0) return row.TurnInEventItem.RowId;
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

            // Skip fates more than N levels above the player.
            if (fate.Level > myLevel + c.LevelsAbovePlayer) continue;

            // Record every fate seen (for the blacklist UI), even if it doesn't qualify.
            NoteSeenFate(fate);

            // Never navigate to a fate the user blacklisted by name.
            if (c.FateBlacklist.Contains(fate.Name.ToString())) continue;

            var type = Classify(fate);

            // One-shot diagnostic per fate id (verbose only): dump classification inputs.
            if (c.VerboseLogging && _diagLogged.Add(fate.FateId))
            {
                uint rule = 0; uint sheetIcon = 0;
                try { if (fate.GameData.ValueNullable is { } r) { rule = r.Rule; sheetIcon = (uint)r.Icon; } }
                catch { }
                Svc.Log.Information($"[FateDiag] '{fate.Name}' id={fate.FateId} -> {type} | Rule={rule} "
                    + $"MapIcon={fate.MapIconId} Icon={fate.IconId} SheetIcon={sheetIcon} HandIn={SafeHandIn(fate)}");
            }

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
