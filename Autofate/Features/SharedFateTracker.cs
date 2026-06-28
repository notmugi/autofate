using ECommons.DalamudServices;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Autofate.Features;

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
    public readonly record struct ZoneProgress(uint TerritoryId, string ZoneName, int Completed)
    {
        /// <summary>Shared FATEs to fully max any zone. Always 66 (ShB/EW: 6+60; DT: 6+20+40).</summary>
        public const int Total = 66;

        /// <summary>Zone is fully done: all 66 shared FATEs completed.</summary>
        public bool IsMaxed => Completed >= Total;
    }

    private static AgentFateProgress* Agent => AgentFateProgress.Instance();
    private const string AddonName = "FateProgress";

    // Internally cached zone data. We open the Shared FATE window ONCE, read everything into this
    // cache, then close the window. All reads go through the cache so we never re-open the window.
    private static readonly List<ZoneProgress> _cache = new();
    private static bool _captured;

    /// <summary>True once we've captured (cached) zone data this session.</summary>
    public static bool HasData() => _captured && _cache.Count > 0;

    /// <summary>
    /// One-shot data load + window close, driven each tick by the controller. Flow:
    ///   1. If we've already captured data, do nothing (return true).
    ///   2. Open the Shared FATE window (MainCommand 84).
    ///   3. Once the agent has populated, read all zones into the cache.
    ///   4. Spam-close the window until it's confirmed gone.
    /// Returns true once data is cached AND the window is closed.
    /// </summary>
    private static long _windowOpenedAtMs;
    private const long HoldOpenMs = 2000; // keep the window open this long before capturing+closing

    public static bool EnsureData()
    {
        if (_captured && _cache.Count > 0 && !AddonVisible()) return true;
        if (ECommons.GenericHelpers.IsOccupied() && !AddonVisible()) return false;

        // Step 1: make sure the window is open. Record when it first became visible so we can hold
        // it open for HoldOpenMs (the agent's per-zone data takes a moment to fully populate).
        if (!AddonVisible())
        {
            _windowOpenedAtMs = 0;
            FireOpenCommand();
            return false; // wait for it to open
        }
        if (_windowOpenedAtMs == 0)
            _windowOpenedAtMs = Environment.TickCount64;

        // Step 2: hold the window open for at least HoldOpenMs so the data is fully loaded, while
        // continuously trying to capture into the cache.
        if (AgentHasData())
            CaptureFromAgent();

        if (Environment.TickCount64 - _windowOpenedAtMs < HoldOpenMs)
            return false; // keep it open a bit longer so the user (and the agent) can settle

        // Step 3: held long enough — capture one final time, then spam-close until gone.
        if (AgentHasData())
            CaptureFromAgent();
        if (EzThrottler.Throttle("AF_SharedFateClose", 200))
            CloseWindow();
        if (AddonVisible())
            return false; // not done until closed
        _windowOpenedAtMs = 0;
        return _captured && _cache.Count > 0;
    }

    /// <summary>
    /// Force a fresh capture: re-open the window, re-read the cache, close again. Used after
    /// completing a fate so per-zone progress reflects the change.
    /// </summary>
    public static void RefreshData(int intervalMs = 60000, bool force = false)
    {
        if (!force && !EzThrottler.Throttle("AF_RefreshFateProgress", intervalMs)) return;
        _captured = false; // invalidate so EnsureData re-captures + re-closes
    }

    private static bool AddonVisible()
        => ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var a) && a != null && a->IsVisible;

    /// <summary>
    /// If the Shared FATE window is open RIGHT NOW (opened by us or by the user), re-read the live
    /// agent into the cache. This is what keeps the displayed values fresh: the zone you're actively
    /// progressing changes every fate, and without a live re-read while the window is up the cache
    /// would show a stale value (e.g. a DT zone "stuck" at its last captured number). Cheap + safe to
    /// call every tick / every UI frame.
    /// </summary>
    public static void CaptureIfWindowOpen()
    {
        if (AddonVisible() && AgentHasData())
            CaptureFromAgent();
    }

    /// <summary>Does the live agent currently expose readable tab/zone data?</summary>
    private static bool AgentHasData()
    {
        var a = Agent;
        if (a == null) return false;
        try { return a->Tabs.Length > 0; }
        catch { return false; }
    }

    /// <summary>Read all zones from the live agent into the internal cache.</summary>
    private static void CaptureFromAgent()
    {
        var a = Agent;
        if (a == null) return;
        try
        {
            _cache.Clear();
            foreach (ref var tab in a->Tabs)
            {
                foreach (ref var z in tab.Zones)
                {
                    if (z.TerritoryTypeId == 0) continue;
                    var exVersion = Svc.Data.GetExcelSheet<TerritoryType>()?
                        .GetRowOrDefault(z.TerritoryTypeId)?.ExVersion.RowId ?? 0;
                    _cache.Add(new ZoneProgress(
                        z.TerritoryTypeId, z.ZoneName.ToString(),
                        CompletedFates(exVersion == 5, z.CurrentRank, z.FateProgress)));
                }
            }
            if (_cache.Count > 0)
            {
                _captured = true;
                Svc.Log.Debug($"[SharedFate] Cached {_cache.Count} zones.");
            }
        }
        catch (Exception e) { Svc.Log.Verbose($"[SharedFate] CaptureFromAgent failed: {e.Message}"); }
    }

    /// <summary>
    /// Close the Shared FATE window. EXACTLY like the working repair/vendor close: fire ONLY the
    /// cancel callback and let the caller retry until AddonVisible() is false. Do NOT also call
    /// Close(true)/Escape in the same pass — Close(true) tears down the addon's callback state so
    /// the Fire lands on a half-dead addon and nothing happens (that's why it stayed open).
    /// </summary>
    private static void CloseWindow()
    {
        if (ECommons.GenericHelpers.TryGetAddonByName<AtkUnitBase>(AddonName, out var a) && a != null && a->IsVisible)
        {
            try { ECommons.Automation.Callback.Fire(a, true, -1); }
            catch (Exception e) { Svc.Log.Verbose($"[SharedFate] Callback.Fire close failed: {e.Message}"); }
        }
    }

    private static void FireOpenCommand(bool force = false)
    {
        if (Agent == null) return;
        if (!force && !EzThrottler.Throttle("AF_OpenFateProgress", 1000)) return;
        try
        {
            FFXIVClientStructs.FFXIV.Client.System.Framework.Framework.Instance()
                ->GetUIModule()->ExecuteMainCommand(84);
        }
        catch (Exception e) { Svc.Log.Verbose($"[SharedFate] ExecuteMainCommand(84) failed: {e.Message}"); }
    }

    /// <summary>
    /// Total shared FATEs completed in a zone. The agent only exposes progress WITHIN the current
    /// rank (FateProgress) plus CurrentRank - there is no direct total - so we add the fates needed
    /// to clear all prior ranks. Caps differ by expansion but always sum to 66:
    ///   ShB/EW (max rank 3): R1->R2 = 6, R2->R3 = 60
    ///   DT     (max rank 4): R1->R2 = 6, R2->R3 = 20, R3->R4 = 40
    /// MaxRank is never read (its agent offset is unreliable); CurrentRank + FateProgress are accurate.
    /// </summary>
    private static int CompletedFates(bool isDawntrail, int currentRank, int progressInRank)
    {
        if (isDawntrail)
        {
            if (currentRank >= 4) return ZoneProgress.Total;
            int[] prior = { 0, 0, 6, 26 };
            return prior[Math.Clamp(currentRank, 1, 3)] + progressInRank;
        }
        if (currentRank >= 3) return ZoneProgress.Total;
        int[] priorSe = { 0, 0, 6 };
        return priorSe[Math.Clamp(currentRank, 1, 2)] + progressInRank;
    }

    /// <summary>Read all cached zones across all expansion tabs.</summary>
    public static List<ZoneProgress> GetAllZones() => new(_cache);

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
