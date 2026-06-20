using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AutoFates.Features;

/// <summary>
/// Reads the in-game Shared FATE tracker (AgentFateProgress, the "Shared FATE" window) to drive
/// per-zone stop logic. Each zone exposes its current rank, max rank, progress and the number of
/// fates still needed for the next rank.
///
/// IMPORTANT: the agent's data is only populated after the Shared FATE window has been opened at
/// least once this session. We force the agent to refresh by briefly showing it (then it stays
/// cached). The controller calls <see cref="EnsureData"/> before relying on the values.
/// </summary>
public static unsafe class SharedFateTracker
{
    public readonly record struct ZoneProgress(
        uint TerritoryId, string ZoneName, int CurrentRank, int MaxRank,
        int FateProgress, int NeededFates)
    {
        /// <summary>This zone's shared-fate rank is maxed out.</summary>
        public bool IsMaxed => MaxRank > 0 && CurrentRank >= MaxRank;
    }

    private static AgentFateProgress* Agent => AgentFateProgress.Instance();

    /// <summary>True once the agent has zone data we can read.</summary>
    public static bool HasData()
    {
        var a = Agent;
        if (a == null) return false;
        try
        {
            var tabs = a->Tabs;
            return tabs.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Make sure the Shared FATE agent has data. If it doesn't, briefly open it to populate the
    /// cache. Returns true once data is available. Throttled so we don't spam the addon.
    /// </summary>
    public static bool EnsureData()
    {
        if (HasData()) return true;
        if (ECommons.GenericHelpers.IsOccupied()) return false;

        var a = Agent;
        if (a == null) return false;

        if (EzThrottler.Throttle("AF_OpenFateProgress", 3000))
        {
            try { a->Show(); }
            catch (Exception e) { Svc.Log.Verbose($"[SharedFate] Show failed: {e.Message}"); }
        }
        return HasData();
    }

    /// <summary>Read all zones across all expansion tabs.</summary>
    public static List<ZoneProgress> GetAllZones()
    {
        var result = new List<ZoneProgress>();
        var a = Agent;
        if (a == null) return result;
        try
        {
            foreach (ref var tab in a->Tabs)
            {
                foreach (ref var z in tab.Zones)
                {
                    if (z.TerritoryTypeId == 0) continue;
                    result.Add(new ZoneProgress(
                        z.TerritoryTypeId,
                        z.ZoneName.ToString(),
                        z.CurrentRank,
                        z.MaxRank,
                        z.FateProgress,
                        z.NeededFates));
                }
            }
        }
        catch (Exception e) { Svc.Log.Verbose($"[SharedFate] GetAllZones failed: {e.Message}"); }
        return result;
    }

    /// <summary>Get the shared-fate progress for one territory, if present.</summary>
    public static ZoneProgress? GetZone(uint territoryId)
    {
        foreach (var z in GetAllZones())
            if (z.TerritoryId == territoryId)
                return z;
        return null;
    }

    /// <summary>True if the given territory's shared-fate rank is maxed (so we should move on).</summary>
    public static bool IsZoneMaxed(uint territoryId)
    {
        var z = GetZone(territoryId);
        return z.HasValue && z.Value.IsMaxed;
    }
}
