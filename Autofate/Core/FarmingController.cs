using System.Linq;
using System.Numerics;
using Autofate.Features;
using Autofate.IPC;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FateState = Dalamud.Game.ClientState.Fates.FateState;

namespace Autofate.Core;

/// <summary>
/// The main fate-farming state machine. Ticked every frame from the plugin's Framework.Update.
/// Decides where to go, which fate to run, hands combat to the configured backend, and runs
/// maintenance (consumables, repair, chocobo, gemstone shopping) between fates.
/// </summary>
public sealed unsafe class FarmingController
{
    public bool Running { get; private set; }
    public FarmState State { get; private set; } = FarmState.Idle;
    public StopReason LastStopReason { get; private set; } = StopReason.None;
    public string StatusText { get; private set; } = "Idle";
    public SessionStats Stats { get; } = new();

    private Configuration C => Plugin.C;

    // Active target fate + zone bookkeeping.
    private ushort _targetFateId;
    private uint _targetTerritory;
    private int _zoneRotationIndex;
    private readonly Dictionary<uint, int> _zoneFatesDone = new();

    // After a gemstone shopping session, remember the gem count so we don't immediately re-enter
    // the shop (continuous-buy entries always "want more"). We only shop again once we've farmed
    // more gems than we had when the last session ended.
    private int _gemCountAfterLastShop = -1;

    // Re-open the Shared FATE window to refresh data every 5 completed fates.
    private int _fatesSinceFateDataRefresh;

    // The farming state we were in before diverting to gemstone shopping. When shopping completes
    // we return directly to this (fate grinding / chocobo loop) instead of routing back through the
    // maintenance check, which would otherwise immediately re-enter the shop.
    // After shopping we always return to SelectingZone — we're standing in the VENDOR's zone, so we
    // must re-pick a farming zone for the current mode and travel back, not look for fates here.
    private FarmState _stateBeforeShop = FarmState.SelectingZone;

    // Zone dwell: when we last saw an active FATE in the current zone. We stay and wait for FATEs
    // to respawn rather than zone-hopping the instant a zone is empty.
    private long _lastFateSeenMs;
    private uint _dwellZone;

    // Collect-fate hand-in math.
    private int _collectTurnedIn;
    private int _collectEstimatedNeeded;
    private int _collectProgressBeforeTurnIn = -1; // fate.Progress sampled before a hand-in, to learn per-turnin value
    // TODO(WIP): for the unfinished collect-fate turn-in math (progress % gained per item).
    //private int _collectPerItemProgress;

    // Smart-mix yield latch: when BMR takes over for a dodge we keep yielding until danger has
    // been fully clear for this settle window, so vnav and BMR don't fight for control.
    private long _yieldUntilMs;
    private const long YieldSettleMs = 600;

    // ---------------------------------------------------------------- lifecycle
    public void Start()
    {
        if (Running) return;

        if (!IPCManager.ValidateBackends(C, out var error))
        {
            Svc.Chat.PrintError($"[Autofate] {error}");
            return;
        }

        Running = true;
        LastStopReason = StopReason.None;
        Stats.Reset();
        _zoneRotationIndex = 0;
        _zoneFatesDone.Clear();
        _dwellZone = 0;
        _lastFateSeenMs = Environment.TickCount64;
        // Reset the shopping latch so we re-evaluate buying fresh on every Start.
        _gemCountAfterLastShop = -1;

        // ON START: open the Shared FATE window once to (re)populate the tracker cache. Invalidating
        // forces EnsureData to re-open, read, and spam-close it on the next ticks.
        _fatesSinceFateDataRefresh = 0;
        Features.SharedFateTracker.RefreshData(force: true);

        // ON START: if we already have the gemstones (>= threshold) and we still need items (any
        // enabled entry's inventory count is below its target), go buy immediately before farming.
        // Otherwise start the normal zone-selection loop.
        if (GemstoneShopper.ShouldShop(C))
        {
            _stateBeforeShop = FarmState.SelectingZone;
            State = FarmState.GemstoneShopping;
            StatusText = "Starting — buying gemstone items first...";
            Svc.Chat.Print("[Autofate] Farming started — buying gemstone items first.");
        }
        else
        {
            State = FarmState.SelectingZone;
            StatusText = "Starting...";
            Svc.Chat.Print("[Autofate] Farming started.");
        }
        IPCManager.StartCombat(C);
        Svc.Log.Information("[Autofate] Started in mode " + C.Mode);
    }

    public void Stop(StopReason reason = StopReason.UserRequested)
    {
        if (!Running) return;
        Running = false;
        LastStopReason = reason;
        State = FarmState.Stopped;
        StatusText = $"Stopped ({reason})";
        Navigator.Stop();
        // Hard-disable EVERY combat/movement IPC (rotation backends, BMR AI + follow/forbid flags,
        // vnavmesh) regardless of the configured backend, so nothing keeps running after Stop.
        IPCManager.ShutdownAll();

        Svc.Chat.Print($"[Autofate] Farming stopped: {reason}.");
        Svc.Log.Information($"[Autofate] Stopped: {reason}");

        // Optional lifestream-to-destination when finishing.
        if (reason != StopReason.Error && C.LifestreamOnFinish && !string.IsNullOrWhiteSpace(C.LifestreamFinishCommand))
        {
            Teleporter.LifestreamCommand(C.LifestreamFinishCommand);
        }
    }

    public void Toggle()
    {
        if (Running) Stop();
        else Start();
    }

    // ---------------------------------------------------------------- main tick
    public void Tick()
    {
        if (!Running) return;
        if (Player.Object == null) return;             // not logged in
        if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]) return;

        Stats.CurrentLevel = Player.Level;
        Stats.SampleGemstones();
        Stats.SampleDeaths();

        // Hard stop triggers (checked every tick).
        if (CheckStopTriggers()) return;

        // Always-on maintenance that can run in parallel with farming.
        ConsumableManager.Tick(C);
        // Skip companion maintenance while stabling: the stable routine WITHDRAWS the chocobo, and
        // auto-summon would immediately try to re-summon it (-> "unable to summon companion here"
        // in housing) and fight our own Withdraw.
        if (State != FarmState.ChocoboLeveling)
            ChocoboManager.Tick(C);

        // In Shared FATEs mode, keep the in-game shared-fate tracker data loaded so zone
        // skip/stop logic has something to read. EnsureData opens the window once (driven by the
        // RefreshData(force) invalidation in Start), caches the data, then spam-closes it.
        if (C.Mode == FarmingMode.SharedFates)
        {
            // EnsureData opens the window once, caches the data, then spam-closes it. We only
            // re-open (invalidate the cache) every 5 completed fates in OnFateFinished.
            Features.SharedFateTracker.EnsureData();
        }

        // SYNC ASAP: if we're physically standing inside a running fate and not yet level-synced,
        // sync immediately — handles the case where the plugin is started while already in a fate.
        if (C.AutoLevelSync && IsInsideAnyRunningFate())
            SyncToFate();

        // Stray-aggro guard: if we're between fates (selecting/traveling) and something hostile is
        // beating on us or our chocobo, drop into ClearingAggro to deal with it first. Checked
        // continuously (not just at fate-end) since aggro can land at any time.
        if ((State == FarmState.SelectingFate || State == FarmState.TravelingToFate
             || State == FarmState.SelectingZone)
            && FateTargeting.GetEnemiesAttackingMe().Count > 0)
        {
            Navigator.Stop();
            State = FarmState.ClearingAggro;
        }

        // FOLLOW-LEADER: skip the whole zone-select/travel state machine. We don't pick or path to
        // our own fates — just follow the leader wherever they go and run whatever fate we land in.
        // (Still allow combat/collect handling once we're physically inside a fate.)
        if (C.FollowPartyLeader
            && State is not (FarmState.InFate or FarmState.CollectTurnIn or FarmState.ClearingAggro
                             or FarmState.TravelingToFate
                             or FarmState.Maintenance or FarmState.ChocoboLeveling or FarmState.GemstoneShopping))
        {
            TickFollowLeader();
            return;
        }

        switch (State)
        {
            case FarmState.SelectingZone: TickSelectingZone(); break;
            case FarmState.TravelingToZone: TickTravelingToZone(); break;
            case FarmState.SelectingFate: TickSelectingFate(); break;
            case FarmState.TravelingToFate: TickTravelingToFate(); break;
            case FarmState.InFate: TickInFate(); break;
            case FarmState.ClearingAggro: TickClearingAggro(); break;
            case FarmState.CollectTurnIn: TickInFate(); break; // collect handled inside InFate
            case FarmState.Maintenance: TickMaintenance(); break;
            case FarmState.ChocoboLeveling: TickChocoboLeveling(); break;
            case FarmState.GemstoneShopping: TickGemstoneShopping(); break;
            default: State = FarmState.SelectingZone; break;
        }
    }

    // ---------------------------------------------------------------- stop triggers
    private bool CheckStopTriggers()
    {
        if (C.StopAtLevel && Player.Level >= C.DesiredLevel)
        {
            Stop(StopReason.LevelReached);
            return true;
        }

        // Leveling mode only: if we die more than twice, something is wrong (overtuned mobs, bad
        // pulls) — stop everything (nav + combat) so we don't keep feeding deaths.
        if (C.Mode == FarmingMode.Leveling && Stats.Deaths > 2)
        {
            Svc.Chat.PrintError("[Autofate] Died more than twice in leveling mode — stopping.");
            Stop(StopReason.TooManyDeaths);
            return true;
        }

        if (C.StopAtGemstoneCount && Stats.GemstonesGained >= C.GemstoneStopCount)
        {
            Stop(StopReason.GemstoneCountReached);
            return true;
        }

        if (C.StopAtChocoboMaxed && C.ChocoboCompanionEnabled && ChocoboManager.ReachedTargetLevel(C))
        {
            Stop(StopReason.ChocoboMaxed);
            return true;
        }

        if (C.StopAtVendorRequirementMet && C.EnableGemstoneShopping && C.GemstoneBuyList.Count > 0
            && GemstoneShopper.AllTargetsMet(C)
            && C.GemstoneBuyList.All(e => !e.Enabled || e.TargetQuantity > 0))
        {
            Stop(StopReason.VendorRequirementMet);
            return true;
        }

        // Collection modes (Atma/Demiatma/Memories/Luminous): stop once the WHOLE required list is
        // in inventory. We track item counts directly (one atma each, 3 demiatma each, 20 memories
        // each, one luminous crystal each).
        if (Data.CollectionRequirements.IsCollectionMode(C.Mode)
            && Data.CollectionRequirements.AllSatisfied(C.Mode))
        {
            Stop(StopReason.CollectionComplete);
            return true;
        }

        // All shared-fate zones maxed (only when we have the tracker data to back it up).
        if (C.Mode == FarmingMode.SharedFates && C.StopWhenAllSharedFatesMaxed
            && Features.SharedFateTracker.HasData())
        {
            var sharedZones = Data.Zones.SharedFateZones(C.SelectedSharedFateExpansions()).Select(z => z.TerritoryId).ToHashSet();
            var tracked = Features.SharedFateTracker.GetAllZones()
                .Where(z => sharedZones.Contains(z.TerritoryId))
                .ToList();
            if (tracked.Count > 0 && tracked.All(z => z.IsMaxed))
            {
                Stop(StopReason.AllSharedFatesMaxed);
                return true;
            }
        }

        // Repair safety: out of dark matter for self-repair.
        if (C.AutoRepair && C.RepairMode == RepairMode.SelfRepair
            && RepairManager.NeedsRepair(C) && !RepairManager.CanSelfRepair())
        {
            Svc.Chat.PrintError("[Autofate] Out of dark matter for self-repair. Stopping.");
            Stop(StopReason.OutOfDarkMatter);
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- zone selection
    private uint[] GetModeZones()
    {
        switch (C.Mode)
        {
            case FarmingMode.SingleZone:
                return C.SingleZoneTerritory != 0 ? new[] { C.SingleZoneTerritory } : Array.Empty<uint>();
            case FarmingMode.Manual:
                return C.ManualZones.Select(z => z.TerritoryId).Where(t => t != 0).ToArray();
            case FarmingMode.Atma:
            case FarmingMode.Demiatma:
            case FarmingMode.LuminousCrystals:
            case FarmingMode.Memories:
            {
                // Only include zones that still host an UNMET collectable. Once every item a zone
                // drops is in inventory, drop it from the rotation so we move on and never return.
                var unmet = Data.CollectionRequirements.UnsatisfiedZones(C.Mode);
                return Data.Zones.ForMode(C.Mode)
                    .Where(z => unmet.Contains(z.PlaceName))
                    .Select(z => z.TerritoryId).Where(t => t != 0).ToArray();
            }
            case FarmingMode.SharedFates:
            {
                var all = Data.Zones.SharedFateZones(C.SelectedSharedFateExpansions()).Select(z => z.TerritoryId);
                // Drive logic from the in-game Shared FATE tracker: skip zones whose shared-fate
                // rank is already maxed (only filter when we actually have the agent data).
                if (C.SharedFateSkipMaxed && Features.SharedFateTracker.HasData())
                    all = all.Where(t => !Features.SharedFateTracker.IsZoneMaxed(t));
                return all.ToArray();
            }
            case FarmingMode.Leveling:
            {
                // Pick the best FATE-grinding zone for the player's level, CAPPED at LevelingZoneCap.
                // So if the cap is 50 we keep farming the level-50 zone even after hitting 50; the
                // actual stop is handled by the Stop Triggers tab (DesiredLevel). As the player
                // levels up (below the cap) the picker advances zones automatically.
                var effLevel = Math.Min(Player.Level, C.LevelingZoneCap);
                var t = Data.LevelingZones.BestTerritoryForLevel(effLevel);
                return t != 0 ? new[] { t } : new[] { Svc.ClientState.TerritoryType };
            }
            default:
                return Array.Empty<uint>();
        }
    }

    private void TickSelectingZone()
    {
        StatusText = "Selecting zone";

        // Maintenance gating before we pick a zone (repair/chocobo/shop take priority).
        if (TryEnterMaintenance()) return;

        var here = Svc.ClientState.TerritoryType;

        // If the current zone is a valid farming zone and has candidate fates, stay.
        var zones = GetModeZones();
        if (zones.Length == 0)
        {
            if (EzThrottler.Throttle("AF_NoZones", 10000))
                Svc.Chat.PrintError("[Autofate] No zones configured for this mode.");
            return;
        }

        // Manual mode rotation handling.
        if (C.Mode == FarmingMode.Manual)
        {
            HandleManualZoneSelection(zones);
            return;
        }

        // SHARED FATES: if the zone we're standing in is already COMPLETE (60 fates), don't farm it —
        // leave and move to the next incomplete zone. GetModeZones() already filters maxed zones, so
        // a complete `here` won't be in `zones`; falling through to the round-robin travel handles
        // the move. (This is the fix for "Lakeland complete but it won't leave".)
        if (C.Mode == FarmingMode.SharedFates && C.SharedFateSkipMaxed
            && Features.SharedFateTracker.HasData()
            && Features.SharedFateTracker.IsZoneMaxed(here))
        {
            if (EzThrottler.Throttle("AF_ZoneComplete", 5000))
                Svc.Log.Information($"[Zone] {Data.Zones.GetTerritoryName(here)} shared FATEs complete (60) — moving on.");
            _targetTerritory = zones[_zoneRotationIndex % zones.Length];
            State = FarmState.TravelingToZone;
            return;
        }

        // If we're already in one of the mode's zones, hand off to fate selection. The dwell
        // logic there waits for FATEs to (re)spawn and only rotates zones after ZoneDwellSeconds
        // of being dry — we must NOT teleport away just because no FATE is active this instant.
        if (zones.Contains(here))
        {
            _targetTerritory = here;
            State = FarmState.SelectingFate;
            return;
        }

        // We're not in a mode zone — travel to the next zone in the round-robin.
        _targetTerritory = zones[_zoneRotationIndex % zones.Length];
        State = FarmState.TravelingToZone;
    }

    private void HandleManualZoneSelection(uint[] zones)
    {
        var entries = C.ManualZones.Where(z => z.TerritoryId != 0).ToList();
        if (entries.Count == 0) return;

        // Find the current entry; advance if its quota is met.
        if (_zoneRotationIndex >= entries.Count)
        {
            if (C.ManualLoop) _zoneRotationIndex = 0;
            else { Stop(StopReason.UserRequested); return; }
        }
        var entry = entries[_zoneRotationIndex];

        if (entry.FatesToRun > 0 && entry.FatesDone >= entry.FatesToRun)
        {
            _zoneRotationIndex++;
            return;
        }

        _targetTerritory = entry.TerritoryId;
        if (Svc.ClientState.TerritoryType == _targetTerritory)
            State = FarmState.SelectingFate;
        else
            State = FarmState.TravelingToZone;
    }

    private void TickTravelingToZone()
    {
        StatusText = $"Traveling to {Data.Zones.GetTerritoryName(_targetTerritory)}";
        if (Teleporter.TravelToTerritory(C, _targetTerritory))
        {
            // Fresh dwell window for the newly-entered zone.
            _dwellZone = 0;
            _lastFateSeenMs = Environment.TickCount64;
            State = FarmState.SelectingFate;
        }
    }

    // ---------------------------------------------------------------- fate selection
    private void TickSelectingFate()
    {
        // Follow-party-leader mode: don't pick our own fate, just follow.
        if (C.FollowPartyLeader)
        {
            TickFollowLeader();
            return;
        }

        if (TryEnterMaintenance()) return;

        var here = Svc.ClientState.TerritoryType;
        var now = Environment.TickCount64;

        // SHARED FATES: if this zone just hit 60 (complete) while we were farming it, stop and go
        // pick the next incomplete zone immediately — don't keep running fates here.
        if (C.Mode == FarmingMode.SharedFates && C.SharedFateSkipMaxed
            && Features.SharedFateTracker.HasData()
            && Features.SharedFateTracker.IsZoneMaxed(here))
        {
            Svc.Log.Information($"[Zone] {Data.Zones.GetTerritoryName(here)} shared FATEs complete (60) — leaving.");
            Navigator.Stop();
            State = FarmState.SelectingZone;
            return;
        }

        // COLLECTION MODES: if every collectable this zone drops is already in inventory at the
        // required count, leave and move to the next zone that still has something we need.
        if (Data.CollectionRequirements.IsCollectionMode(C.Mode)
            && Data.CollectionRequirements.ZoneSatisfied(C.Mode, Data.Zones.GetTerritoryName(here)))
        {
            Svc.Log.Information($"[Zone] {Data.Zones.GetTerritoryName(here)} collectables complete — moving on.");
            Navigator.Stop();
            State = FarmState.SelectingZone;
            return;
        }

        // Reset the dwell timer whenever we (re)enter a zone, so we give each zone a full dwell
        // window to spawn a FATE before considering rotating away.
        if (_dwellZone != here)
        {
            _dwellZone = here;
            _lastFateSeenMs = now;
        }

        StatusText = "Selecting fate";
        var best = FateSelector.PickBest(C);
        if (best == null)
        {
            // No valid fate right now. FATEs respawn every few minutes, so DWELL in this zone and
            // wait rather than instantly teleporting away. Only rotate after the zone has been dry
            // for ZoneDwellSeconds (0 = never rotate, stay forever).
            var zones = GetModeZones();
            var dwellMs = C.ZoneDwellSeconds * 1000L;
            var dryFor = now - _lastFateSeenMs;

            if (zones.Length > 1 && C.ZoneDwellSeconds > 0 && dryFor >= dwellMs)
            {
                Svc.Log.Debug($"[Zone] No FATEs for {dryFor / 1000}s in {Data.Zones.GetTerritoryName(here)}; rotating to next zone.");
                _zoneRotationIndex++;
                _lastFateSeenMs = now;   // reset so the next zone gets a fresh dwell window
                _dwellZone = 0;
                State = FarmState.SelectingZone;
            }
            else
            {
                var remain = Math.Max(0, (dwellMs - dryFor) / 1000);
                StatusText = zones.Length > 1 && C.ZoneDwellSeconds > 0
                    ? $"Waiting for FATEs ({remain}s before rotating zones)"
                    : "Waiting for FATEs to spawn";
            }
            return;
        }

        // We have a live FATE here — refresh the dwell timer.
        _lastFateSeenMs = now;

        _targetFateId = best.Value.Fate.FateId;
        _startedFateId = 0; // new fate -> allow the start-NPC talk again
        _collectTurnedIn = 0;
        _collectEstimatedNeeded = 0;
        State = FarmState.TravelingToFate;
    }

    private void TickTravelingToFate()
    {
        var fate = FateSelector.GetFateById(_targetFateId);
        if (fate == null || fate.State == FateState.Ended || fate.State == FateState.Failed)
        {
            Navigator.Stop();
            State = FarmState.SelectingFate;
            return;
        }

        var me0 = Player.Object;
        var distToCenter = me0 == null ? float.MaxValue : Vector3.Distance(me0.Position, fate.Position);

        // Drop into the CENTER of the fate, not the edge. We consider ourselves "arrived" only when
        // we're near the center (or as close as navmesh can get us). ArrivalRange is small so the
        // combat backend engages from the middle of the pack.
        const float arrivalRange = 4f;
        if (distToCenter <= arrivalRange)
        {
            Navigator.Stop();

            // Dismount before fighting — mounted/flying blocks the combat backend from engaging.
            if (Features.MountManager.IsMounted)
            {
                StatusText = $"Arrived at fate: {fate.Name} (dismounting)";
                Features.MountManager.Dismount();
                return; // re-check next tick; once dismounted we proceed below
            }

            // Sync once we're actually inside (avoids syncing to pass-through fates).
            if (C.AutoLevelSync) SyncToFate();

            Stats.OnFateAttempted();
            IPCManager.StartCombat(C); // (re)engage now that we're grounded inside the fate
            State = FarmState.InFate;
            return;
        }

        // Navigate to the fate CENTER (closest valid spot navmesh can reach). Allow mount/flight
        // only while still outside the ring; once inside, walk the rest so we don't re-mount for the
        // short hop to center (which would cause a mount/dismount loop).
        StatusText = $"Traveling to fate: {fate.Name}";
        var insideRing = distToCenter <= fate.Radius;
        Navigator.MoveTo(C, fate.Position, arrivalRange, allowMount: !insideRing);
    }

    private bool IsInsideFate(IFate fate)
    {
        var me = Player.Object;
        if (me == null) return false;
        return Vector3.Distance(me.Position, fate.Position) <= fate.Radius;
    }

    /// <summary>True if we're physically standing inside any currently-running fate's ring.</summary>
    private static bool IsInsideAnyRunningFate() => FateSelector.GetCurrentFate() != null;

    // Tracks the fate-start NPC interaction so we don't spam-interact while dialogue is up.
    private long _fateNpcInteractedMs;
    private ushort _startedFateId; // fate we've already done the start-NPC talk for (don't repeat)
    private long _fateStartConfirmedMs; // when we clicked Yes to start a fate (escort follow waits ~3s after)
    private ulong _escortNpcId; // cached escort NPC object id (FateId can flicker to 0 mid-fate)
    private bool _escortChasing; // hysteresis: are we currently chasing the escort NPC?
    private long _dismountedForNpcMs; // when we dismounted to talk to a fate NPC (settle delay)

    /// <summary>
    /// Some fates require talking to a start NPC (orange "!") to begin: it pops Talk dialogue we
    /// click through, then a Yes/No to start. Returns true while we're handling that (caller should
    /// return and let it finish). Reuses ECommons addon helpers (Talk.Click / SelectYesno.Yes).
    /// </summary>
    private unsafe bool TryStartFateViaNpc(IFate fate)
    {
        // 1) If a Yes/No prompt is up, confirm it (this is the "Assist the guard?" start prompt).
        //    Gate on IsAddonReady (NOT just IsVisible): during the open/close animation the addon
        //    is visible but its YesButton pointer is still null, which NRE'd inside SelectYesno.Yes.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "SelectYesno", out var yn) && ECommons.GenericHelpers.IsAddonReady(yn))
        {
            if (EzThrottler.Throttle("AF_FateYes", 600))
            {
                try { new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectYesno((nint)yn).Yes(); }
                catch (Exception e) { Svc.Log.Verbose($"[Controller] FateYes failed (addon mid-transition): {e.Message}"); }
                _fateStartConfirmedMs = Environment.TickCount64; // fate activates ~3s after this
            }
            return true;
        }

        // 2) If Talk dialogue is up, click through it.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "Talk", out var talk) && ECommons.GenericHelpers.IsAddonReady(talk))
        {
            if (EzThrottler.Throttle("AF_FateTalk", 250))
            {
                try { new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Talk((nint)talk).Click(); }
                catch (Exception e) { Svc.Log.Verbose($"[Controller] FateTalk failed (addon mid-transition): {e.Message}"); }
            }
            return true;
        }

        // 3) No dialogue open. Look for the fate-start NPC and interact with it. Pass the fate
        // radius so we can also match an un-started "!" NPC (FateId=0) standing in the ring.
        var npcRadius = fate.Radius > 0 ? fate.Radius : 0f;
        var npc = FateTargeting.FindFateStartNpc(_targetFateId, npcRadius);
        if (npc == null) return false; // nothing to start -> normal combat fate

        // GROUNDED GUARD: land + dismount before targeting/interacting with the start NPC. We don't
        // want to dive at the quest giver from the air or fire Interact while mounted.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            Navigator.Stop();
            Features.MountManager.Dismount();
            _dismountedForNpcMs = Environment.TickCount64; // start post-dismount settle timer
            StatusText = $"Landing/dismounting before talking to {npc.Name}";
            return true;
        }

        // SETTLE after dismount: dismounting plays a short landing/jump animation. Interacting too
        // early throws "cannot execute action while jumping", so wait ~1s and until we're not
        // jumping/occupied before targeting + interacting.
        if (_dismountedForNpcMs != 0 && Environment.TickCount64 - _dismountedForNpcMs < 1000)
        {
            StatusText = $"Settling before talking to {npc.Name}";
            return true;
        }
        if (Player.IsJumping)
        {
            StatusText = $"Waiting to land before talking to {npc.Name}";
            return true;
        }

        // Walk to the NPC if out of interact range.
        var me = Player.Object;
        if (me == null) return false;
        var dist = Vector3.Distance(me.Position, npc.Position);
        if (dist > 4f)
        {
            if (!ECommons.GenericHelpers.IsOccupied())
                Navigator.MoveTo(C, npc.Position, 3f, allowMount: false);
            StatusText = $"Approaching fate NPC: {npc.Name}";
            return true;
        }

        // In range: target + interact (throttled).
        Navigator.Stop();
        if (Player.IsAnimationLocked || !Player.Interactable) return true;
        if (Svc.Targets.Target?.GameObjectId != npc.GameObjectId)
            Svc.Targets.Target = npc;
        if (EzThrottler.Throttle("AF_FateInteract", 1500))
        {
            StatusText = $"Starting fate via NPC: {npc.Name}";
            FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()
                ->InteractWithObject(((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address), false);
            _fateNpcInteractedMs = Environment.TickCount64;
            _startedFateId = _targetFateId; // remember we've talked to this fate's start NPC
        }
        return true;
    }

    private void SyncToFate()
    {
        // Never sync while mounted/airborne — be fully grounded before doing anything in a fate.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying) return;
        if (!EzThrottler.Throttle("AF_FateSync", 1000)) return;
        try
        {
            var fm = FateManager.Instance();
            if (fm == null) return;
            // Only sync if not already synced (LevelSync toggles).
            if (fm->SyncedFateId == 0)
                fm->LevelSync();
        }
        catch (Exception e) { Svc.Log.Verbose($"[Controller] SyncToFate failed: {e.Message}"); }
    }

    // ---------------------------------------------------------------- in-fate
    private void TickInFate()
    {
        var fate = FateSelector.GetFateById(_targetFateId);
        if (fate == null || fate.State == FateState.Ended || fate.State == FateState.Failed)
        {
            OnFateFinished();
            return;
        }

        // GROUNDED GUARD (covers everything below: sync, NPC-start, collect, and combat). Be fully
        // off the mount and on the ground before doing ANYTHING in a fate. Only exception is when a
        // dialogue addon is already up — we still click through that so we don't get stuck.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            var dlgUp = ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("Talk", out var td) && ECommons.GenericHelpers.IsAddonReady(td)
                     || ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var yd) && ECommons.GenericHelpers.IsAddonReady(yd);
            if (!dlgUp)
            {
                Navigator.Stop();
                Features.MountManager.Dismount();
                StatusText = $"Landing/dismounting before engaging: {fate.Name}";
                return;
            }
        }

        var type = FateSelector.Classify(fate);
        StatusText = $"In fate: {fate.Name} ({type}) {fate.Progress}%";

        // FATE-START NPC: some fates (escort, many "guard"/defend fates, AND collect fates) only
        // begin once you talk to a start NPC carrying the orange "!". Classification (Rule/icon) is
        // unreliable and often tags these as Battle, so DON'T gate solely on type. Trigger NPC-start
        // while the fate hasn't progressed and there are no fate enemies to fight yet — the
        // unambiguous "waiting for you to start it" state.
        //
        // COLLECT fates use the SAME "!" NPC for both starting AND turning in, so we only do the
        // start-talk on the initial join: 0 progress AND we hold none of the collectable yet. Once
        // we've started/collected, HandleCollectFate drives the turn-ins instead.
        var dialogueOpen = ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("Talk", out var t) && ECommons.GenericHelpers.IsAddonReady(t)
                        || ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var y) && ECommons.GenericHelpers.IsAddonReady(y);
        var notStarted = fate.Progress <= 0 && FateTargeting.CountFateEnemies(_targetFateId) == 0;
        if (type == FateType.Collect)
        {
            // Only intercept for the initial join. If a turn-in dialogue is mid-flow we still let it
            // pass through here (clicking Talk/Yes is the same), but we must NOT block once items
            // exist or progress has begun — that's handled by HandleCollectFate.
            var collectItemId = FateSelector.GetCollectItemId(fate);
            var haveItems = collectItemId != 0 && InventoryUtil.GetItemCount(collectItemId) > 0;
            // Only on the very first join, and only if we haven't already talked to this fate's
            // start NPC (the latch stops the accept->talk->accept loop).
            var freshJoin = fate.Progress <= 0 && !haveItems && _startedFateId != _targetFateId;
            if ((freshJoin || dialogueOpen) && TryStartFateViaNpc(fate)) return;
        }
        else
        {
            // Latch prevents re-talking to a start NPC we've already engaged (accept loop).
            var canStart = notStarted && _startedFateId != _targetFateId;
            if ((canStart || dialogueOpen) && TryStartFateViaNpc(fate)) return;
        }

        // Keep ourselves inside the fate ring if we've drifted out. Don't do this when the combat
        // backend (BMR AI) is driving movement, and never re-mount for these short hops.
        if (!BmrMovementActive() && !IsInsideFate(fate) && !ECommons.GenericHelpers.IsOccupied())
            Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.6f), allowMount: false);

        // BEHAVIORAL escort detection: classification (Rule/icon) is unreliable and often tags
        // escort fates as Battle (e.g. "The Ceruleum Road"). The unambiguous signal is a friendly
        // NPC carrying our fate id — that's the escorted NPC. If one exists (or we've cached it),
        // ALWAYS run the escort follow handler regardless of how Classify tagged the fate.
        var hasEscortNpc = type == FateType.Escort
                           || _escortNpcId != 0
                           || FateTargeting.GetDefendedFriendlies(_targetFateId).Count > 0;

        switch (type)
        {
            case FateType.Collect:
                HandleCollectFate(fate);
                break;
            case FateType.Escort:
                HandleEscortFate(fate);
                break;
            default:
                if (hasEscortNpc) { HandleEscortFate(fate); break; }
                // Battle / Boss / Defend: combat backend does the work. We just make sure we have a target.
                EnsureCombatEngaged(fate);
                break;
        }

        // Fate completed by progress.
        if (fate.Progress >= 100)
            OnFateFinished();
    }

    private void EnsureCombatEngaged(IFate fate)
    {
        var me = Player.Object;
        if (me == null) return;

        // GROUNDED GUARD: never target / engage while mounted or in the air. The combat backend
        // can't act while mounted, and targeting mid-flight makes us dive at mobs from above. Land
        // and dismount FIRST, then bail this tick — we re-evaluate targeting once we're on foot.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            Navigator.Stop();
            Features.MountManager.Dismount();
            StatusText = $"Landing/dismounting before engaging: {fate.Name}";
            return;
        }

        // APPROACH FIRST: the very first thing we do is close distance to the nearest fate enemy.
        // If we're dropped far from the mob (or it isn't aggroed), we must NOT set it as the target
        // yet — doing so makes the rotation start plinking with a ranged auto/skill from across the
        // fate, and that cast trips IsOccupied() which blocks our movement, leaving us standing
        // still. So while we're out of engage range we keep NO target and just navigate in.
        {
            var nearest = FateTargeting.GetNearestFateEnemy(_targetFateId);
            if (nearest != null)
            {
                var nd = Vector3.Distance(me.Position, nearest.Position);
                var ne = Math.Max(2.5f, nearest.HitboxRadius + 2.5f);
                if (nd > ne)
                {
                    // Respect BMR's AOE dodge while approaching.
                    if (BmrMovementActive())
                    {
                        if (IPCManager.YieldMovementForDodge() || IPCManager.DangerPresent())
                        {
                            _yieldUntilMs = Environment.TickCount64 + YieldSettleMs;
                            Navigator.Stop();
                            return;
                        }
                        IPCManager.SetBmrMovement(false); // take movement so vnav can close in
                    }
                    // Clear any target so the rotation doesn't cast from range (which would root us).
                    if (Svc.Targets.Target != null) Svc.Targets.Target = null;
                    if (!ECommons.GenericHelpers.IsOccupied())
                        Navigator.MoveTo(C, nearest.Position, ne, allowMount: false);
                    StatusText = $"Approaching {nearest.Name}";
                    return;
                }
            }
        }

        // STICKY TARGET (every 1s): keep us on an ENEMY, never a friendly.
        //  - If we're already targeting a live ENEMY  -> leave it alone (don't yank mid-cast).
        //  - Otherwise (friendly / dead / nothing)     -> switch to a fate enemy.
        // We only do this while synced to the fate (i.e. actually fighting it). A friendly under
        // attack is handled implicitly: switching to an enemy and killing it peels the threat.
        if (EzThrottler.Throttle("AF_Sticky", 1000))
        {
            var cur = Svc.Targets.Target as IBattleNpc;
            var onLiveEnemy = cur != null && FateTargeting.IsAttackableEnemy(cur);
            if (!onLiveEnemy)
            {
                // Prefer an enemy attacking a friendly (peel), else the nearest fate enemy.
                var threat = FateTargeting.GetActiveDefendThreat(_targetFateId);
                var pick = threat ?? FateTargeting.GetNearestFateEnemy(_targetFateId);
                if (pick != null)
                {
                    Svc.Targets.Target = pick;
                    Svc.Log.Debug($"[Combat] Sticky retarget -> '{pick.Name}'.");
                }
            }
        }

        // CRITICAL: every backend (including BMR AI) needs a TARGET to fight — none of them will
        // go hunt fate mobs on their own. We pick the nearest live mob belonging to THIS fate
        // (matched by GameObject.FateId) and feed it to the combat backend.
        if (!EzThrottler.Throttle("AF_AcquireTarget", 300)) return;

        // SMART MIX (BMR movement backend): vnavmesh walks us to the enemy, BMR handles AOE
        // avoidance. To stop vnav and BMR fighting for control (walk out of AOE -> walk back in ->
        // walk out again), we use a latch with hysteresis:
        //   - Enter yield as soon as a dodge is imminent (ShouldYieldForDodge).
        //   - STAY yielded while ANY danger is present (DangerPresent), and for a short settle
        //     window after danger fully clears, before vnav is allowed to resume pathing.
        if (BmrMovementActive())
        {
            var now = Environment.TickCount64;
            if (IPCManager.YieldMovementForDodge())
            {
                // Imminent danger — hand movement to BMR and (re)arm the settle timer.
                _yieldUntilMs = now + YieldSettleMs;
                Navigator.Stop();
                return;
            }
            if (IPCManager.DangerPresent())
            {
                // Danger still active (zone hasn't expired) — keep yielding, keep timer armed.
                _yieldUntilMs = now + YieldSettleMs;
                Navigator.Stop();
                return;
            }
            if (now < _yieldUntilMs)
            {
                // Danger just cleared; wait out the settle window so we don't immediately path
                // back into a zone that BMR only just pulled us out of.
                Navigator.Stop();
                return;
            }
        }

        var isDefend = FateSelector.Classify(fate) == FateType.Defend;
        var target = FateTargeting.EnsureFateTarget(_targetFateId, defendPriority: isDefend);

        if (target != null && EzThrottler.Throttle("AF_TargetLog", 3000))
            Svc.Log.Debug($"[Combat] Targeting fate mob '{target.Name}' ({FateTargeting.CountFateEnemies(_targetFateId)} fate mobs nearby).");

        // WHEN BMR OWNS MOVEMENT: let BMR do everything — it paths to the target AND dodges AOEs.
        // We only pick the target (for mass-pull, the nearest un-aggroed mob to widen the pull) and
        // otherwise stay completely out of movement. We must NOT forbid BMR movement or issue vnav
        // here, or BMR can't dodge (that was the "not dodging AOEs at all" regression). Make sure
        // movement is handed back in case a previous non-BMR path left it forbidden.
        if (BmrMovementActive())
        {
            if (target == null) return; // BMR will reposition; nothing to target yet

            // MASS PULL (no range limit): while under the pile cap and there's an un-aggroed fate
            // enemy, target the nearest one to widen the pull. Once capped, keep the chosen target.
            if (C.MassPull)
            {
                var aggroed = FateTargeting.CountAggroedFateEnemies(_targetFateId);
                if (aggroed < C.MassPullMaxPile)
                {
                    var pull = FateTargeting.GetNearestUnaggroedFateEnemy(_targetFateId);
                    if (pull != null) target = pull;
                }
            }

            if (Player.Object is not { IsCasting: true }
                && (Svc.Targets.Target is not IBattleNpc bc || bc.GameObjectId != target.GameObjectId))
                Svc.Targets.Target = target;

            // BMR won't CHASE a fate enemy that isn't aggroed onto us yet — it only dodges and
            // fights what's already engaged. So when the target is out of engage range, WE vnav
            // toward it (taking movement from BMR); once in range we hand movement back to BMR so it
            // can resume AOE dodging. This is the "navigate to the closest enemy at any range" fix.
            var bmrDist = Vector3.Distance(me.Position, target.Position);
            var bmrEngage = Math.Max(2.5f, target.HitboxRadius + 2.5f);
            if (bmrDist > bmrEngage && !ECommons.GenericHelpers.IsOccupied())
            {
                IPCManager.SetBmrMovement(false);      // take movement so vnav can path to the mob
                Navigator.MoveTo(C, target.Position, bmrEngage, allowMount: false);
            }
            else
            {
                IPCManager.SetBmrMovement(true);       // in range -> hand back so BMR dodges/fights
                FateTargeting.StartAutoAttack(target); // open combat so the backend engages
            }
            return;
        }

        if (target == null)
        {
            // No fate mobs nearby. Move toward the fate center to find action.
            if (!ECommons.GenericHelpers.IsOccupied()
                && Vector3.Distance(me.Position, fate.Position) > fate.Radius * 0.4f)
            {
                Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.4f), allowMount: false);
            }
            return;
        }

        // --- Below here BMR does NOT own movement (we returned above if it did). We drive movement
        // ourselves via vnav. ---

        // MASS PULL (no range limit): while under the pile cap and there's an un-aggroed fate enemy,
        // walk to the nearest one to body-pull it; once capped, fall through to engage the target.
        if (C.MassPull)
        {
            var aggroed = FateTargeting.CountAggroedFateEnemies(_targetFateId);
            if (aggroed < C.MassPullMaxPile)
            {
                var pull = FateTargeting.GetNearestUnaggroedFateEnemy(_targetFateId);
                if (pull != null) target = pull;
            }
        }

        // Walk into melee/casting range of the target so the rotation backend (Wrath/RSR) can attack.
        var dist = Vector3.Distance(me.Position, target.Position);
        var engageRange = Math.Max(2.5f, target.HitboxRadius + 2.5f);
        if (dist > engageRange && !ECommons.GenericHelpers.IsOccupied())
        {
            Navigator.MoveTo(C, target.Position, engageRange, allowMount: false);
        }
        else
        {
            Navigator.Stop();
            // In range: force-open combat ourselves (auto-attack) so the backend engages even if it
            // would otherwise wait to be hit / has no working lease.
            FateTargeting.StartAutoAttack(target);
        }
    }

    private bool BmrMovementActive() => IPCManager.BmrHandlesMovement(C);

    // ---------------------------------------------------------------- collect fates
    // TODO(WIP): Collect fates are disabled in the UI — the turn-in loop needs in-game calibration.
    // Grab labeled ground items (EventObj with the FateId), turn them in to the "!" NPC, and infer
    // how many remain from the progress delta. We don't need to "complete" the fate, just hit 100%
    // (or run dry) then leave.
    private unsafe void HandleCollectFate(IFate fate)
    {
        var collectItemId = FateSelector.GetCollectItemId(fate);
        var have = collectItemId != 0 ? InventoryUtil.GetItemCount(collectItemId) : 0;

        // --- 1) If the Item Request (turn-in) window is open, drive the hand-in. ---
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "Request", out var req) && req != null && req->IsVisible)
        {
            HandleRequestWindow((nint)req, fate, collectItemId);
            return;
        }

        // --- 2) Decide whether to turn in now. ---
        // Turn in once we have a batch (initial sample = CollectInitialTurnIn) OR once we hold the
        // estimated remaining amount. Until we've learned the per-item value, use the initial batch.
        var batch = _collectEstimatedNeeded > 0
            ? Math.Min(have, _collectEstimatedNeeded - _collectTurnedIn)
            : C.CollectInitialTurnIn;

        var fateProgress = fate.Progress;
        var done = fateProgress >= 100;
        if (done)
        {
            // Threshold hit — leave for the next fate (collect fates don't need completing).
            OnFateFinished();
            return;
        }

        if (collectItemId != 0 && have > 0 && have >= Math.Min(batch, C.CollectInitialTurnIn))
        {
            // Walk to the turn-in NPC and interact to open the Request window.
            var npc = FateTargeting.GetCollectTurnInNpc(_targetFateId);
            if (npc != null)
            {
                var me2 = Player.Object;
                if (me2 != null && Vector3.Distance(me2.Position, npc.Position) > 4f)
                {
                    if (!ECommons.GenericHelpers.IsOccupied())
                        Navigator.MoveTo(C, npc.Position, 3f, allowMount: false);
                    StatusText = $"Collect: turning in (to {npc.Name})";
                    return;
                }
                Navigator.Stop();
                if (Player.IsAnimationLocked || !Player.Interactable) return;
                if (Svc.Targets.Target?.GameObjectId != npc.GameObjectId)
                    Svc.Targets.Target = npc;
                if (EzThrottler.Throttle("AF_CollectInteract", 1500))
                {
                    _collectProgressBeforeTurnIn = fateProgress; // sample to learn per-item value
                    FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()
                        ->InteractWithObject(((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address), false);
                }
                return;
            }
        }

        // --- 3) Otherwise gather: go pick up the nearest ground collectable. ---
        // COMBAT FIRST: if we're in combat (mobs aggroed on us), finish the fight before collecting.
        // Picking up items mid-combat gets us beaten on and interrupts pickups. Engage the attacker
        // and bail this tick; we'll resume collecting once combat is clear.
        if (InCombat() && FateTargeting.GetEnemiesAttackingMe().Count > 0)
        {
            EnsureCombatEngaged(fate);
            StatusText = $"Collect: clearing combat before gathering";
            return;
        }

        var item = FateTargeting.GetNearestCollectable(_targetFateId);
        if (item != null)
        {
            var me = Player.Object;
            if (me != null && Vector3.Distance(me.Position, item.Position) > 3f)
            {
                if (!ECommons.GenericHelpers.IsOccupied())
                    Navigator.MoveTo(C, item.Position, 2f, allowMount: false);
                StatusText = $"Collect: grabbing {item.Name} (have {have})";
                return;
            }
            Navigator.Stop();
            if (Player.IsAnimationLocked || !Player.Interactable) return;
            if (Svc.Targets.Target?.GameObjectId != item.GameObjectId)
                Svc.Targets.Target = item;
            if (EzThrottler.Throttle("AF_CollectPickup", 1200))
                FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()
                    ->InteractWithObject(((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)item.Address), false);
            return;
        }

        // No ground items right now: kill mobs only if they aggro (handled by ClearingAggro guard),
        // else hold near center and wait for collectables to respawn.
        StatusText = $"Collect fate: {fate.Name} {fateProgress}% (have {have}, item {collectItemId})";
        if (!BmrMovementActive() && !ECommons.GenericHelpers.IsOccupied())
            Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.5f), allowMount: false);
    }

    /// <summary>
    /// Drive the Item Request (collect turn-in) window: fill the slot with the collect item and
    /// click Hand Over. Per the user's flow: right-click the request slot, select the item to fill,
    /// then Hand Over. We use ECommons' Request master for Hand Over + AgentInventoryContext to fill.
    /// Also learns how much fate-progress each turn-in grants to estimate the remaining count.
    /// </summary>
    private unsafe void HandleRequestWindow(nint reqAddon, IFate fate, uint collectItemId)
    {
        // Guard: the Request addon can be visible while its buttons/nodes aren't built yet. Touching
        // rq.IsHandOverEnabled (-> HandOverButton->IsEnabled) on a null button NREs, so verify the
        // addon is fully ready AND the hand-over button exists before reading it.
        if (!ECommons.GenericHelpers.IsAddonReady((FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)reqAddon))
            return;

        var rq = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Request(reqAddon);

        bool handOverReady;
        try { handOverReady = rq.HandOverButton != null && rq.IsHandOverEnabled; }
        catch (Exception e) { Svc.Log.Verbose($"[Collect] Request not ready: {e.Message}"); return; }

        // If hand-over is enabled, the slot is filled -> click it.
        if (handOverReady)
        {
            if (EzThrottler.Throttle("AF_HandOver", 800))
            {
                try { rq.HandOver(); }
                catch (Exception e) { Svc.Log.Verbose($"[Collect] HandOver failed: {e.Message}"); return; }
                // Learn per-item progress from the delta after the hand-in lands (next ticks).
                _collectTurnedIn += Math.Max(1, C.CollectInitialTurnIn);
            }
            return;
        }

        // Slot not filled yet: place the collect item into the request slot. The user's flow is:
        // right-click the item in the inventory -> it fills the open Request slot. We open the
        // item's context menu against the Request addon, which fills the slot.
        if (collectItemId != 0 && EzThrottler.Throttle("AF_FillRequest", 800))
        {
            var addonId = GetAddonId("Request");
            InventoryUtil.OpenItemContextMenu(collectItemId, addonId);
        }
    }

    private static unsafe uint GetAddonId(string name)
    {
        return ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(name, out var a) && a != null
            ? (uint)a->Id : 0u;
    }

    // ---------------------------------------------------------------- escort fates
    private void HandleEscortFate(IFate fate)
    {
        // Escort: the NPC walks a route and the fate center MOVES with it. We must actively chase the
        // NPC/center (it walks away otherwise) while letting the rotation kill threats.
        StatusText = $"Escort fate: {fate.Name} {fate.Progress}%";

        // Target only our-fate enemies so the rotation engages threats. FateId-filtered, so we never
        // pick an overlapping fate's mobs.
        var threat = FateTargeting.GetActiveDefendThreat(_targetFateId)
                     ?? FateTargeting.GetNearestFateEnemy(_targetFateId);
        if (threat != null
            && (Svc.Targets.Target is not IBattleNpc cur || cur.GameObjectId != threat.GameObjectId))
            Svc.Targets.Target = threat;
        if (threat != null) FateTargeting.StartAutoAttack(threat);

        // After clicking Yes to start the fate, the escort NPC takes ~3s to (re)spawn and begin
        // walking. Wait that out before chasing so we don't path to the pre-start NPC position.
        if (_fateStartConfirmedMs != 0 && Environment.TickCount64 - _fateStartConfirmedMs < 3000)
        {
            StatusText = $"Escort starting: {fate.Name} (waiting for NPC)";
            return;
        }

        // Resolve the escort NPC. CRITICAL: cache it ONCE at the start of the fate and only ever
        // follow THAT object id. Do NOT re-pick every tick — when our escort route passes through
        // another fate, GetDefendedFriendlies could return that fate's friendly and we'd start
        // following the wrong NPC. So: if we don't have a cached id yet, grab the nearest our-fate
        // friendly and lock onto it; otherwise always resolve the cached id by object.
        IBattleNpc? escortNpc;
        if (_escortNpcId == 0)
        {
            escortNpc = FateTargeting.GetDefendedFriendlies(_targetFateId).FirstOrDefault();
            if (escortNpc != null) _escortNpcId = escortNpc.GameObjectId; // lock it in for the fate
        }
        else
        {
            // Always follow the locked NPC by id (survives FateId flicker AND passing-through fates).
            escortNpc = Svc.Objects.FirstOrDefault(o => o.GameObjectId == _escortNpcId) as IBattleNpc;
        }

        // NEVER fall back to fate.Position: the ring center TRAILS the walking NPC, so chasing it
        // runs us backward to the rear of the fate. If we genuinely can't find the NPC, hold.
        if (escortNpc == null || escortNpc.IsDead)
        {
            // Can't resolve the NPC right now — hold position (don't run to the trailing center).
            Navigator.Stop();
            return;
        }
        var followPos = escortNpc.Position;

        // MOVEMENT: WE own movement for the entire escort. The old code toggled BMR movement on/off
        // every tick based on distance — that made the character chase a mob (BMR) then get yanked
        // back to the NPC (us), flapping back and forth ("walking away and back over and over").
        // Instead: forbid BMR movement ONCE (set-guarded so no chat spam) and always drive movement
        // ourselves toward the NPC. BMR still fights/dodges in place; the rotation kills our target.
        // Enemies aggroed on us get dragged to the NPC because we stay glued to it.
        if (BmrMovementActive())
            IPCManager.SetBmrMovement(false); // change-guarded internally; only fires once

        var me = Player.Object;
        if (me == null) { Navigator.Stop(); return; }
        var distToNpc = Vector3.Distance(me.Position, followPos);

        // Stay glued to the NPC (mobs always attack the NPC, so we don't chase them). Hysteresis so
        // we don't flap: start chasing past 5y, stop once within 2.5y. FollowMoveTo re-issues as the
        // NPC drifts.
        const float chaseStart = 5f;
        const float chaseStop = 2.5f;
        if (_escortChasing)
        {
            if (distToNpc <= chaseStop) { _escortChasing = false; Navigator.Stop(); }
            else Navigator.FollowMoveTo(C, followPos, chaseStop);
        }
        else
        {
            if (distToNpc > chaseStart) { _escortChasing = true; Navigator.FollowMoveTo(C, followPos, chaseStop); }
            else Navigator.Stop();
        }
    }

    private void OnFateFinished()
    {
        Stats.OnFateCompleted();
        Navigator.Stop();
        _yieldUntilMs = 0; // clear the smart-mix yield latch

        // In Shared FATEs mode, re-open the Shared FATE window to repopulate per-zone progress, but
        // only every 5 completed fates (opening the window is disruptive, so we don't do it every
        // fate). EnsureData will re-capture and spam-close it on the following ticks.
        if (C.Mode == FarmingMode.SharedFates)
        {
            _fatesSinceFateDataRefresh++;
            if (_fatesSinceFateDataRefresh >= 5)
            {
                _fatesSinceFateDataRefresh = 0;
                Features.SharedFateTracker.RefreshData(force: true);
            }
        }

        // Per-zone counters (manual mode quota).
        var terr = Svc.ClientState.TerritoryType;
        _zoneFatesDone.TryGetValue(terr, out var done);
        _zoneFatesDone[terr] = done + 1;

        if (C.Mode == FarmingMode.Manual)
        {
            var entry = C.ManualZones.FirstOrDefault(z => z.TerritoryId == terr);
            if (entry != null) entry.FatesDone++;
        }

        _targetFateId = 0;
        _startedFateId = 0;
        _fateStartConfirmedMs = 0;
        _escortNpcId = 0;
        _escortChasing = false;
        _dismountedForNpcMs = 0;
        // Escort may have forbidden BMR movement — hand it back so the next fate behaves normally.
        if (BmrMovementActive()) IPCManager.SetBmrMovement(true);

        // If we're still in combat (we pulled stray non-fate enemies — possibly hitting our chocobo
        // rather than us), clear them before moving on so we don't get stuck.
        if (InCombat())
        {
            State = FarmState.ClearingAggro;
            return;
        }

        State = FarmState.SelectingFate;
    }

    /// <summary>True if the player is flagged in combat.</summary>
    private static bool InCombat()
    {
        var me = Player.Object;
        return me != null
            && (me.StatusFlags & Dalamud.Game.ClientState.Objects.Enums.StatusFlags.InCombat) != 0;
    }

    /// <summary>Kill any stray hostiles until we're out of combat, then resume fate selection.</summary>
    private void TickClearingAggro()
    {
        // GROUNDED GUARD: land + dismount before targeting/engaging stray aggro.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            Navigator.Stop();
            Features.MountManager.Dismount();
            StatusText = "Landing/dismounting before clearing aggro";
            return;
        }

        // Primary: enemies targeting us OR our chocobo. Secondary: anything hostile while still
        // flagged in combat. Done when both are clear.
        var attackers = FateTargeting.GetEnemiesAttackingMe();
        var hostile = attackers.Count > 0 ? attackers[0]
                    : (InCombat() ? FateTargeting.GetNearestHostile() : null);

        if (hostile == null)
        {
            Navigator.Stop();
            IPCManager.StopCombat(C);
            State = FarmState.SelectingFate;
            return;
        }

        var attacker = hostile;
        if (!(Svc.Targets.Target is IBattleNpc cur && FateTargeting.IsAttackableEnemy(cur)
              && (cur.GameObjectId == attacker.GameObjectId)))
            Svc.Targets.Target = attacker;

        StatusText = $"Clearing stray aggro: {attacker.Name}";
        IPCManager.StartCombat(C); // make the rotation backend fight it

        // Walk into range if the backend isn't moving us.
        var me = Player.Object;
        if (me != null && !BmrMovementActive() && !ECommons.GenericHelpers.IsOccupied())
        {
            var dist = Vector3.Distance(me.Position, attacker.Position);
            var engageRange = Math.Max(2.5f, attacker.HitboxRadius + 2.5f);
            if (dist > engageRange)
                Navigator.MoveTo(C, attacker.Position, engageRange, allowMount: false);
            else
                Navigator.Stop();
        }
    }

    // ---------------------------------------------------------------- follow leader
    private void TickFollowLeader()
    {
        if (Svc.Party.Length == 0)
        {
            StatusText = "Follow: not in a party";
            if (EzThrottler.Throttle("AF_NoParty", 8000))
                Svc.Log.Information($"[Follow] Not in a party (Party.Length=0). LeaderIdx={Svc.Party.PartyLeaderIndex}.");
            return;
        }

        var leader = GetPartyLeaderObject();
        var me = Player.Object;

        // If WE are the leader (or the leader object is us), there's nobody to follow — just hold
        // and let combat/sync handle whatever fate we're standing in.
        if (leader == null || me == null || leader.GameObjectId == me.GameObjectId)
        {
            StatusText = leader == null ? "Follow: leader object not loaded (out of range?)" : "Follow: you are the leader";
            if (EzThrottler.Throttle("AF_FollowSelf", 5000))
                Svc.Log.Information($"[Follow] No distinct leader. Party.Length={Svc.Party.Length}, LeaderIdx={Svc.Party.PartyLeaderIndex}, leaderObj={(leader == null ? "null" : leader.Name.ToString())}.");
            var f0 = FateSelector.GetCurrentFate();
            if (f0 != null && C.AutoLevelSync) SyncToFate();
            return;
        }

        // HAND OFF TO THE FATE MACHINE: if the leader has dropped us inside a running fate (and that
        // fate type is enabled), stop following and run it via the normal InFate flow — which lands,
        // dismounts, starts the fate via NPC if needed, syncs, and fights. When the fate ends,
        // OnFateFinished sets State=SelectingFate, and the top-of-tick follow redirect resumes us
        // here to follow the leader again.
        var fate = FateSelector.GetCurrentFate();
        if (fate != null && (C.EnabledFateTypes & FateSelector.Classify(fate)) != 0)
        {
            Navigator.Stop();
            _targetFateId = fate.FateId;
            _startedFateId = 0;
            _collectTurnedIn = 0;
            _collectEstimatedNeeded = 0;
            // Route through TravelingToFate (NOT straight to InFate) so we walk to the fate CENTER —
            // same dropoff as normal farming. GetCurrentFate triggers at the ring EDGE, and InFate
            // only re-centers when outside the ring, so jumping straight to InFate left us stranded
            // at the edge. TravelingToFate drives to arrivalRange (4y) of center.
            State = FarmState.TravelingToFate;
            StatusText = $"Follow: entering fate {fate.Name}";
            return;
        }

        var distToLeader = Vector3.Distance(me.Position, leader.Position);
        StatusText = $"Following {leader.Name} ({distToLeader:0}y)";

        // ALWAYS mount up while following (not in a fate) so we keep pace with the leader's mount.
        // Mount but DON'T return — fall through to FollowMoveTo so vnav starts pathing (and takes
        // off into flight) the same frame instead of waiting a tick.
        if (!MountManager.IsMounted && MountManager.CanMountHere
            && distToLeader > C.FollowDistance + 2f
            && !ECommons.GenericHelpers.IsOccupied())
        {
            MountManager.Mount(C);
        }

        // ALWAYS use vnavmesh to chase the leader's LIVE position. (We deliberately do NOT use
        // BMR's /bmrai follow here — that only works when BMR AI is enabled, which it isn't in
        // follow mode, so it silently did nothing.)
        Navigator.FollowMoveTo(C, leader.Position, C.FollowDistance);
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetPartyLeaderObject()
    {
        var idx = Svc.Party.PartyLeaderIndex;
        if (idx >= Svc.Party.Length) return null;
        var member = Svc.Party[(int)idx];
        if (member == null) return null;

        // member.GameObject is null when the leader isn't in the local object table yet. Fall back
        // to resolving by the party member's EntityId/ObjectId against the live object table.
        if (member.GameObject != null) return member.GameObject;

        var wantEntity = member.EntityId; // entity id of the party member
        foreach (var obj in Svc.Objects)
        {
            if (obj.EntityId == wantEntity) return obj;
        }
        return null;
    }

    // ---------------------------------------------------------------- maintenance gating
    /// <summary>If any maintenance task is needed, switch into the appropriate state. Returns true if entered.</summary>
    private bool TryEnterMaintenance()
    {
        // Chocobo leveling has priority when the chocobo needs stabling/feeding.
        if (C.ChocoboLevelingEnabled && ChocoboStableRoutine.NeedsAttention(C))
        {
            ChocoboStableRoutine.ResetSteps(); // fresh stable step machine each time we enter
            State = FarmState.ChocoboLeveling;
            return true;
        }

        // Repair.
        if (C.AutoRepair && RepairManager.NeedsRepair(C))
        {
            State = FarmState.Maintenance;
            return true;
        }

        // Gemstone shopping. Don't re-enter right after a session unless we've farmed more gems
        // (prevents the open->close->reinteract loop caused by continuous-buy always "wanting more").
        if (GemstoneShopper.ShouldShop(C)
            && (_gemCountAfterLastShop < 0 || Features.InventoryUtil.GetGemstoneCount() > _gemCountAfterLastShop))
        {
            // Remember where we came from so we can return straight to it when shopping is done.
            // Always return to SelectingZone after shopping — we'll be in the vendor's zone and need
            // to re-pick + travel back to a farming zone for the current mode.
            _stateBeforeShop = FarmState.SelectingZone;
            State = FarmState.GemstoneShopping;
            return true;
        }

        return false;
    }

    private void TickMaintenance()
    {
        StatusText = "Maintenance: repair";
        if (!RepairManager.NeedsRepair(C))
        {
            State = FarmState.SelectingZone;
            return;
        }

        // Self-repair flow (no crafter gearset needed — dark matter repairs any gear on any class).
        if (C.RepairMode == RepairMode.SelfRepair)
        {
            if (!RepairManager.CanSelfRepair())
            {
                Stop(StopReason.OutOfDarkMatter);
                return;
            }
            // RunSelfRepair returns true once everything is repaired AND the window is closed.
            if (RepairManager.RunSelfRepair(C))
                State = FarmState.SelectingZone;
            return;
        }

        // TODO(WIP): Mender NPC repair isn't automated (greyed in UI). Needs vendor routing +
        // repair-all-from-NPC flow. For now, warn and continue.
        if (EzThrottler.Throttle("AF_NpcRepair", 30000))
            Svc.Chat.PrintError("[Autofate] NPC repair not yet automated; switch to self-repair.");
        State = FarmState.SelectingZone;
    }

    private void TickChocoboLeveling()
    {
        StatusText = "Chocobo leveling";
        if (ChocoboStableRoutine.Tick(C))
        {
            // Routine finished (either fed & ready, or chocobo maxed).
            if (C.StopAtChocoboMaxed && ChocoboManager.ReachedTargetLevel(C))
                Stop(StopReason.ChocoboMaxed);
            else
                State = FarmState.SelectingZone;
        }
    }

    private unsafe void TickGemstoneShopping()
    {
        StatusText = "Gemstone shopping";

        // TOP-OF-TICK COMPLETION GUARD (works whether the shop is open or not): if every enabled
        // buy entry has reached its target item count, we're DONE. Close the shop if it's still up,
        // then go straight back to farming. This runs BEFORE the navigate/interact fall-through so
        // we never re-open the shop after finishing.
        var hasContinuous = C.GemstoneBuyList.Any(e => e.Enabled && e.TargetQuantity == 0);
        if (!hasContinuous && GemstoneShopper.AllTargetsMet(C))
        {
            _gemCountAfterLastShop = Features.InventoryUtil.GetGemstoneCount();
            if (GemstoneShopper.ShopOpen())
            {
                if (EzThrottler.Throttle("AF_CloseShop", 300))
                    GemstoneShopper.CloseShop();
                return; // wait for it to actually close before leaving
            }
            State = _stateBeforeShop;
            return;
        }

        // If the shop addon is open, buy until done. FORCIBLE COMPLETION: the instant there's
        // nothing left we can AND want to buy (every entry is at its target threshold or
        // unaffordable), yank control back to whatever we were doing before. This check only runs
        // while the shop is OPEN (item costs are only readable then) so we don't bail mid-travel.
        if (GemstoneShopper.ShopOpen())
        {
            if (GemstoneShopper.BuyingComplete(C))
            {
                _gemCountAfterLastShop = Features.InventoryUtil.GetGemstoneCount();
                // Keep firing CloseShop until the window is actually gone, THEN leave the state.
                // Leaving early would orphan an open shop window (the bug you saw).
                if (EzThrottler.Throttle("AF_CloseShop", 300))
                {
                    if (GemstoneShopper.CloseShop())
                        State = _stateBeforeShop;
                }
                return;
            }
            GemstoneShopper.PurchaseTick(C);
            return;
        }

        // Interacting with the vendor often pops Talk dialogue (greeting) BEFORE the shop addon
        // opens — spam-click through it so we reach the menu. Gate on IsAddonReady to avoid NREs.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("Talk", out var gtalk)
            && ECommons.GenericHelpers.IsAddonReady(gtalk))
        {
            if (EzThrottler.Throttle("AF_GemTalk", 200))
            {
                try { new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Talk((nint)gtalk).Click(); }
                catch (Exception e) { Svc.Log.Verbose($"[Gemstone] Talk click failed: {e.Message}"); }
            }
            return;
        }

        // No captured vendor location -> we can't auto-travel. Tell the user how to set it once.
        if (!C.VendorPositionSet)
        {
            if (EzThrottler.Throttle("AF_GemNoVendor", 30000))
                Svc.Chat.Print("[Autofate] Open the gemstone vendor and click 'Add' on an item in the Gemstone tab to capture its location for auto-travel.");
            if (!GemstoneShopper.ShouldShop(C)) State = FarmState.SelectingZone;
            return;
        }

        // STEP 1: teleport to the vendor's zone (nearest aetheryte) if we're not there yet.
        if (Svc.ClientState.TerritoryType != C.VendorTerritory)
        {
            StatusText = $"Traveling to gemstone vendor in {Data.Zones.GetTerritoryName(C.VendorTerritory)}";
            Teleporter.TravelToTerritory(C, C.VendorTerritory);
            return;
        }

        // STEP 2: in the vendor's zone — dismount, then navigate to the vendor and interact.
        var me = Player.Object;
        if (me == null) return;

        // Find the live vendor NPC (by captured DataId) so we interact with the exact object;
        // fall back to the captured position if the NPC isn't loaded yet.
        var vendor = FindGemstoneVendor();
        var dest = vendor?.Position ?? C.VendorPosition;
        var dist = Vector3.Distance(me.Position, dest);

        if (dist > 4f)
        {
            StatusText = $"Navigating to {(string.IsNullOrEmpty(C.VendorName) ? "gemstone vendor" : C.VendorName)}";
            Navigator.MoveTo(C, dest, 3f);
            return;
        }

        // In range — dismount before interacting.
        Navigator.Stop();
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            Features.MountManager.Dismount();
            return;
        }

        if (vendor == null)
        {
            // We're at the captured spot but the NPC isn't found (moved patch / wrong capture).
            if (EzThrottler.Throttle("AF_GemVendorMissing", 15000))
                Svc.Chat.PrintError("[Autofate] Reached the vendor spot but couldn't find the vendor NPC. Re-capture it in the Gemstone tab.");
            return;
        }

        if (Player.IsAnimationLocked || !Player.Interactable) return;
        if (Svc.Targets.Target?.GameObjectId != vendor.GameObjectId)
            Svc.Targets.Target = vendor;
        if (EzThrottler.Throttle("AF_GemInteract", 1500))
        {
            StatusText = $"Opening shop: {vendor.Name}";
            FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()
                ->InteractWithObject(((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)vendor.Address), false);
        }
    }

    /// <summary>Find the captured gemstone vendor NPC nearby (by BaseId/DataId), or null.</summary>
    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindGemstoneVendor()
    {
        if (C.VendorDataId == 0) return null;
        var me = Player.Object;
        if (me == null) return null;
        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestSq = float.MaxValue;
        foreach (var o in Svc.Objects)
        {
            if (o.BaseId != C.VendorDataId) continue;
            var d = Vector3.DistanceSquared(me.Position, o.Position);
            if (d < bestSq) { bestSq = d; best = o; }
        }
        return best;
    }
}
