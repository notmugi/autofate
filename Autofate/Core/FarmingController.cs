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
    /// <summary>Last validation error from a failed Start (missing required plugin). Shown on Status tab.</summary>
    public string LastStartError { get; private set; } = string.Empty;
    /// <summary>Set when Start fails validation; the UI consumes this to switch to the Status tab once.</summary>
    public bool ForceStatusTab { get; set; }
    public string StatusText { get; private set; } = "Idle";
    public SessionStats Stats { get; } = new();

    private Configuration C => Plugin.C;

    // ---------------------------------------------------------------- diagnostics
    // Throttled, area-tagged logging for our recurring problem areas (NPC interaction, combat,
    // movement, collect, escort). Gated by the single C.VerboseLogging toggle (Status tab). Each
    // area is throttled by a key so the log isn't flooded.
    private void Diag(string area, string key, string msg)
    {
        if (!C.VerboseLogging) return;
        if (!EzThrottler.Throttle($"AFDiag_{area}_{key}", 500)) return;
        Svc.Log.Info($"[Diag/{area}] {msg}");
    }

    // Active target fate + zone bookkeeping.
    private ushort _targetFateId;
    private uint _targetTerritory;
    private int _zoneRotationIndex;
    private readonly Dictionary<uint, int> _zoneFatesDone = new();

    // After a gemstone shopping session, remember the gem count so we don't immediately re-enter
    // the shop (continuous-buy entries always "want more"). We only shop again once we've farmed
    // more gems than we had when the last session ended.
    private int _gemCountAfterLastShop = -1;

    // One-shot latch for the CURRENT shop visit. Set the instant a visit is judged complete
    // (everything bought / drained to threshold / capped). While latched we NEVER re-interact the
    // vendor — we only close the window and leave the state. Cleared on entry to a fresh visit
    // (StartShopping) so the next legitimate trip works. This is the fix for the open->buy->close->
    // re-interact->reopen loop: ShouldShop() can still read true after buying, so we must not let
    // the NPC-interact fall-through fire again within the same visit.
    private bool _shoppingDone;

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

    // Re-open the Shared FATE window to refresh data every 5 completed fates.
    private int _fatesSinceFateDataRefresh;

    // Collect-fate hand-in: we hand in fixed batches (DWD/BMR-style) rather than trying to "learn"
    // the per-item progress value, which was fragile and never calibrated. Hold until we have a
    // full batch, hand it in, repeat until the fate hits 100% (or runs dry of ground items).
    private const int CollectBatchSize = 10; // collect fates reward "gold" at 10 turned in

    // Tracks whether the rotation backend is currently running (so we toggle it only on transitions,
    // not every tick). During collect HandIn/Pickup we STOP the backend so it doesn't fight us for
    // the target — that target tug-of-war was the "flicks between the NPC and an enemy" bug.
    private bool _combatBackendActive;

    // Fate-travel stuck detection: if we barely move for >2s while traveling to the fate dropoff,
    // the spot is unreachable -> re-roll a NEW random LANDABLE point in the ring and go to that.
    private System.Numerics.Vector3 _fateStuckLastPos;
    private long _fateStuckLastSampleMs;
    private const long FateStuckWindowMs = 2000;
    private const float FateStuckMinMove = 2f;

    // In-fate UNLANDABLE recovery: rarely we land on a spot that isn't actually landable, so the
    // dismount keeps failing and we sit MOUNTED + stationary inside the ring. If that persists past
    // FateStuckWindowMs, re-roll a NEW random LANDABLE point in the ring and navigate to it. Once
    // we're DISMOUNTED we've made it (no re-roll). Dedicated sampler so it can't clash with the
    // TravelingToFate stuck sampler.
    private System.Numerics.Vector3 _inFateMountPos;
    private long _inFateMountMs;

    // Grounded-stuck escape: while on foot and navigating, if we move < GroundStuckMinMove over
    // GroundStuckWindowMs we're wedged on geometry. Back straight out ~5y, then regenerate the path.
    private System.Numerics.Vector3 _groundStuckLastPos;
    private long _groundStuckLastMs;
    // Jump phase that runs BEFORE backing out: jump, wait for grounded, jump again, then wait the
    // SAME window; only if still not moved do we fall through to back-out + renav.
    private int _groundJumpPhase;                 // 0=idle, 1=did first jump (await grounded), 2=did second jump (await window)
    private System.Numerics.Vector3 _groundJumpStartPos;
    private long _groundJumpWaitUntilMs;
    private System.Numerics.Vector3? _groundNudgeTarget; // back-out point we're driving to
    private long _groundNudgeUntilMs;                    // safety timeout for the nudge
    private bool _groundNudgeFly;                        // back-out nudge should use flight pathing
    private const long GroundStuckWindowMs = 2000;
    private const float GroundStuckMinMove = 2f;
    private void SetCombatBackend(bool active)
    {
        if (_combatBackendActive == active) return;
        _combatBackendActive = active;
        if (active) IPCManager.StartCombat(C);
        else IPCManager.StopCombat(C);
    }


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
            LastStartError = error;
            ForceStatusTab = true; // tell the UI to jump to the Status tab so the user sees the error
            return;
        }
        LastStartError = string.Empty;

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
            EnterShopping();
            StatusText = "Starting — buying gemstone items first...";
            Svc.Chat.Print("[Autofate] Farming started — buying gemstone items first.");
        }
        else
        {
            State = FarmState.SelectingZone;
            StatusText = "Starting...";
            Svc.Chat.Print("[Autofate] Farming started.");
        }
        SetCombatBackend(true);
        TextAdvanceIPC.Enable(); // hand ALL our dialogue (Talk/Yes-No/reward/cutscene/turn-in) to TextAdvance
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
        TextAdvanceIPC.Disable();     // release any collect turn-in control
        _combatBackendActive = false; // re-sync the combat latch so the next Start re-issues

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
            // EnsureData opens the window once, caches the data, then spam-closes it.
            Features.SharedFateTracker.EnsureData();
            // If the window is open (ours or the user's), re-read live so the zone we're actively
            // progressing updates instead of showing a stale cached value.
            Features.SharedFateTracker.CaptureIfWindowOpen();
        }

        // SYNC ASAP: if we're physically standing inside a running fate and not yet level-synced,
        // sync immediately — handles the case where the plugin is started while already in a fate.
        if (C.AutoLevelSync && IsInsideAnyRunningFate())
            SyncToFate();

        // Stray-aggro guard: if we're between fates (selecting/traveling) and something hostile is
        // beating on us or our chocobo, drop into ClearingAggro to deal with it first. Checked
        // continuously (not just at fate-end) since aggro can land at any time.
        if ((State == FarmState.SelectingFate || State == FarmState.TravelingToFate
             || State == FarmState.SelectingZone || State == FarmState.TravelingToZone)
            && (InCombat() || FateTargeting.GetEnemiesAttackingMe().Count > 0))
        {
            Navigator.Stop();
            State = FarmState.ClearingAggro;
        }

        // Grounded-stuck escape (back out + regenerate) — consumes the tick if active.
        if (TickGroundedStuck()) return;

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

        // Chocobo maxed -> stop. CRITICAL: do NOT fire this while we're still stabling. Feeding the
        // final onion flips Rank to 20 mid-fetch; if we stopped here we'd halt the plugin before the
        // fetch completes and leave the chocobo stuck in the stable. Only stop once we've left the
        // stable flow (the routine doesn't finish until the chocobo is fetched back out).
        if (C.StopAtChocoboMaxed && C.ChocoboCompanionEnabled && ChocoboManager.ReachedTargetLevel(C)
            && State != FarmState.ChocoboLeveling)
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

        // POST-COMPLETION GRACE: if we just finished a fate, hold briefly before committing to a
        // FAR fate so a chained replacement spawning at/near our spot can be picked instead. A
        // nearby fate (likely the replacement) is taken immediately.
        if (now < _postFateGraceUntilMs && best.Value.Distance > PostFateNearbyDist)
        {
            StatusText = "Waiting for a possible replacement FATE...";
            return;
        }
        _postFateGraceUntilMs = 0; // committing now -> clear the grace window

        _targetFateId = best.Value.Fate.FateId;
        _startedFateId = 0; // new fate -> allow the start-NPC talk again
        _fateNpcInteractedMs = 0; // clear the post-interact hold for the new fate
        ResetPerFateState(); // clear escort/collect carry-over from any previous fate
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
        if (me0 == null) return;

        var type = FateSelector.Classify(fate);
        var insideRing = Vector3.Distance(me0.Position, fate.Position) <= fate.Radius;

        // NPC-start fates (Escort/Defend, or a not-yet-started Collect with no enemies): travel to
        // the START NPC. Arrival for these is "inside the ring" — TickInFate then walks the rest of
        // the way to the NPC and drives the talk.
        var startNpcNeeded = type is FateType.Escort or FateType.Defend
            || (type == FateType.Collect && FateTargeting.GetNearestFateEnemy(_targetFateId) == null);
        if (startNpcNeeded)
        {
            if (insideRing) { ArriveAtFate(fate); return; }
            var npc = FateTargeting.FindFateStartNpc(_targetFateId, fate.Radius);
            var dest = npc?.Position ?? fate.Position;
            StatusText = $"Traveling to fate NPC: {fate.Name}";
            Navigator.MoveTo(C, dest, 3.5f, allowMount: true);
            return;
        }

        // Combat fates (and Collect fates already underway): travel to a RANDOM LANDABLE interior
        // point. ARRIVAL = inside the fate boundaries AND within ~4y of that dropoff. That decisively
        // ends fate-travel nav, drops us, and hands off to enemy navigation (TickInFate). We do NOT
        // arrive on inside-ring alone (that dropped us at the edge / re-navved).
        _fateDropoff ??= RandomPointInFate(fate);
        if (insideRing && Vector3.Distance(me0.Position, _fateDropoff.Value) <= 4f)
        {
            ArriveAtFate(fate);
            return;
        }

        // STUCK -> re-roll (ONLY while travelling to the dropoff): if we barely move for >2s the
        // dropoff is unreachable; pick a new random landable interior point and head there instead.
        var nowMs = Environment.TickCount64;
        if (_fateStuckLastSampleMs == 0) { _fateStuckLastSampleMs = nowMs; _fateStuckLastPos = me0.Position; }
        else if (nowMs - _fateStuckLastSampleMs >= FateStuckWindowMs)
        {
            if (Vector3.Distance(me0.Position, _fateStuckLastPos) < FateStuckMinMove)
            {
                Navigator.Stop();
                _fateDropoff = RandomPointInFate(fate);
                Diag("Movement", "fatestuck", $"stuck >{FateStuckWindowMs}ms -> new random dropoff {_fateDropoff}");
            }
            _fateStuckLastSampleMs = nowMs;
            _fateStuckLastPos = me0.Position;
        }

        StatusText = $"Traveling to fate: {fate.Name}";
        Navigator.MoveTo(C, _fateDropoff.Value, 4f, allowMount: true);
    }

    /// <summary>
    /// We're at our dropoff (inside the ring / reached the random point). Dismount, then sync +
    /// engage. If we've been here &gt;5s and are STILL mounted, vnav has likely wedged on an
    /// unreachable spot, so pick a NEW random interior point and renav to break the stall.
    /// </summary>
    private void ArriveAtFate(IFate fate)
    {
        // STOP navigating the instant we're considered arrived — never re-path inside the ring (that
        // re-nav was the land<->navigate loop). Then just dismount; once grounded, start the fate.
        Navigator.Stop();

        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            StatusText = $"Arrived at fate: {fate.Name} (dismounting)";
            Features.MountManager.Dismount(); // throttled internally; re-checked next tick
            return;
        }

        // Grounded -> begin the fate.
        if (C.AutoLevelSync) SyncToFate();
        Stats.OnFateAttempted();
        Features.ChocoboManager.SampleXpAtFateStart();
        SetCombatBackend(true);
        State = FarmState.InFate;
    }

    private static readonly Random _fateRng = new();

    /// <summary>A random point inside the fate ring (within ~75% of the radius so we stay
    /// comfortably inside). Used as the dropoff target instead of the (often-unreachable) center.</summary>
    private static Vector3 RandomPointInFate(IFate fate)
    {
        // Pick a random X/Z inside the ring and resolve the ACTUAL landable floor there. We do NOT
        // keep the ring-center Y: on sloped/multi-level terrain that height is wrong for the chosen
        // X/Z and is exactly what made us aim into the floor. SnapToFloor searches downward from high
        // above and returns a LANDABLE point only, so every candidate is a real spot we can stand on.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var r = fate.Radius * 0.75f * MathF.Sqrt((float)_fateRng.NextDouble());
            var theta = (float)(_fateRng.NextDouble() * Math.PI * 2);
            var xz = new Vector3(fate.Position.X + r * MathF.Cos(theta), fate.Position.Y,
                                 fate.Position.Z + r * MathF.Sin(theta));

            if (SnapLandable(xz) is { } pt
                && Vector3.Distance(new Vector3(pt.X, 0, pt.Z),
                                    new Vector3(fate.Position.X, 0, fate.Position.Z)) <= fate.Radius)
                return pt;
        }

        // Nothing landable sampled -> snap the ring centre to the floor (or use it as-is).
        return SnapLandable(fate.Position) ?? fate.Position;
    }

    /// <summary>Project a point onto a LANDABLE navmesh floor. Searches downward from well above so
    /// we hit the top surface, and NEVER accepts an unlandable poly (so we never drop somewhere we
    /// can't stand). Returns null if no landable floor is found.</summary>
    private static Vector3? SnapLandable(Vector3 p)
        => Autofate.IPC.NavmeshIPC.PointOnFloor(new Vector3(p.X, p.Y + 50f, p.Z), false, 5f);
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
    // After firing a collect interact (NPC turn-in OR ground pickup) we latch a cooldown and the
    // object id we interacted with. The game opens addons / starts cast animations on a SERVER
    // ROUNDTRIP, so the addon isn't visible on the next tick — if we re-fire the interact in that
    // gap we CANCEL the in-flight one (the Request window opens then instantly closes, forever).
    // This is exactly the guard the reference executor uses (don't re-interact while a previous
    // interact is still resolving). We hold off re-interacting the SAME object until this expires.
    private long _collectInteractCooldownMs;
    private ulong _collectInteractObjId;
    private const long CollectInteractCooldownMs = 2000;
    private ushort _startedFateId; // fate we've already done the start-NPC talk for (don't repeat)
    private long _fateStartConfirmedMs;
    // After a FATE completes, MANY fates immediately spawn a chained replacement at (or near) the
    // same spot. Don't instantly commit to navigating off to a far fate — hold briefly so a nearby
    // replacement can spawn and be picked (it'll be the closest). A nearby fate is taken at once.
    private long _postFateGraceUntilMs;
    private const long PostFateGraceMs = 5000;     // how long to wait for a replacement after a clear
    private const float PostFateNearbyDist = 50f;  // a fate within this range is taken immediately (no grace wait)
    private ulong _escortNpcId; // cached escort NPC object id (FateId can flicker to 0 mid-fate)
    private bool _escortChasing; // hysteresis: are we currently chasing the escort NPC?
    // ESCORT detection by RING MOVEMENT: an escort fate's ring (fate.Position) MOVES as the escorted
    // NPC walks; a normal fate's ring is fixed. We sample the position on first sight and flag the
    // fate as a follow fate once the ring has drifted past a threshold. This is reliable where the
    // sheet Rule and MotivationNpc are not (many non-escort fates expose a MotivationNpc).
    private Vector3 _fateInitialPos; // first-seen ring center for the current fate
    private bool _fatePosSampled;    // have we sampled _fateInitialPos yet?
    private bool _ringMovedFollow;   // ring has moved enough -> treat as follow fate (latched)
    private Vector3? _fateDropoff;   // randomized dropoff spot inside the current fate ring
    private const float RingMoveFollowThreshold = 8f; // yalms of ring drift to call it an escort
    private long _dismountedForNpcMs; // when we dismounted to talk to a fate NPC (settle delay)

    /// <summary>
    /// Some fates require talking to a start NPC (orange "!") to begin: it pops Talk dialogue then a
    /// Yes/No to start. TextAdvance (taken session-wide at Start) advances/confirms ALL of that for
    /// us; we only have to do the interact. Returns true while we're handling it (caller should
    /// return and let it finish). The manual Talk/Yes clicking below is a FALLBACK only for when
    /// TextAdvance isn't installed.
    /// </summary>
    private unsafe bool TryStartFateViaNpc(IFate fate)
    {
        // 1) Dialogue handling. When TextAdvance holds control it advances the Talk window and
        //    confirms the Yes/No start prompt itself — we just record the confirm time and wait.
        var yesOpen = ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "SelectYesno", out var yn) && ECommons.GenericHelpers.IsAddonReady(yn);
        var talkOpen = ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "Talk", out var talk) && ECommons.GenericHelpers.IsAddonReady(talk);

        // ALWAYS click the fate-start Yes/No ourselves. TextAdvance does NOT confirm this box — it's
        // a generic FATE-start level-warning prompt ("Get stabbed? — recommended level 94"), the same
        // class of arbitrary Yes/No TA ignores (like the gemstone-exchange box). If we deferred it to
        // TA it would sit open forever. Gate on IsAddonReady (NOT just IsVisible): during the
        // open/close animation the addon is visible but its YesButton pointer is still null (NREs).
        if (yesOpen)
        {
            if (EzThrottler.Throttle("AF_FateYes", 600))
            {
                try { new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectYesno((nint)yn).Yes(); }
                catch (Exception e) { Svc.Log.Verbose($"[Controller] FateYes failed (addon mid-transition): {e.Message}"); }
                _fateStartConfirmedMs = Environment.TickCount64; // fate activates ~3s after this
            }
            return true;
        }
        // Talk window: let TextAdvance advance it when it holds control; otherwise click it ourselves.
        if (talkOpen)
        {
            if (!TextAdvanceIPC.ControlActive && EzThrottler.Throttle("AF_FateTalk", 250))
            {
                try { new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Talk((nint)talk).Click(); }
                catch (Exception e) { Svc.Log.Verbose($"[Controller] FateTalk failed (addon mid-transition): {e.Message}"); }
            }
            return true;
        }

        Diag("NPC", "dialogue", $"yesOpen={yesOpen} talkOpen={talkOpen} taControl={TextAdvanceIPC.ControlActive}");

        // 3) No dialogue open. Look for the fate-start NPC and interact with it. Pass the fate
        // radius so we can also match an un-started "!" NPC (FateId=0) standing in the ring.
        var npcRadius = fate.Radius > 0 ? fate.Radius : 0f;
        var npc = FateTargeting.FindFateStartNpc(_targetFateId, npcRadius);
        if (npc == null) { Diag("NPC", "find", $"no start NPC found (fateId={_targetFateId} radius={npcRadius:F1}) -> treating as combat fate"); return false; }

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
        Diag("NPC", "approach", $"npc='{npc.Name}' dist={dist:F1} occupied={ECommons.GenericHelpers.IsOccupied()} animLock={Player.IsAnimationLocked} interactable={Player.Interactable}");
        if (dist > 4f)
        {
            if (!ECommons.GenericHelpers.IsOccupied())
                Navigator.MoveTo(C, npc.Position, 3f, allowMount: false);
            StatusText = $"Approaching fate NPC: {npc.Name}";
            return true;
        }

        // In range: target + interact (throttled).
        Navigator.Stop();
        if (Player.IsAnimationLocked || !Player.Interactable) { Diag("NPC", "interact", $"in range but blocked (animLock={Player.IsAnimationLocked} interactable={Player.Interactable})"); return true; }
        if (Svc.Targets.Target?.GameObjectId != npc.GameObjectId)
            Svc.Targets.Target = npc;
        if (EzThrottler.Throttle("AF_FateInteract", 1500))
        {
            StatusText = $"Starting fate via NPC: {npc.Name}";
            Diag("NPC", "interact", $"FIRING InteractWithObject on '{npc.Name}' (id={npc.GameObjectId})");
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
                var meM = Player.Object;
                var nowM = Environment.TickCount64;

                // Track how long we've been mounted + stationary here. If we landed on a spot we
                // can't actually dismount on, re-roll a new LANDABLE point and navigate to it.
                if (_inFateMountMs == 0) { _inFateMountMs = nowM; _inFateMountPos = meM?.Position ?? default; }
                else if (meM != null && Vector3.Distance(meM.Position, _inFateMountPos) >= FateStuckMinMove)
                {
                    _inFateMountMs = nowM; _inFateMountPos = meM.Position; // moving (descending/repositioning) -> keep waiting
                }
                else if (nowM - _inFateMountMs >= FateStuckWindowMs)
                {
                    _fateDropoff = RandomPointInFate(fate);
                    _inFateMountMs = nowM; _inFateMountPos = meM?.Position ?? default;
                    Navigator.Stop();
                    Navigator.MoveTo(C, _fateDropoff.Value, 4f, allowMount: false);
                    Diag("Movement", "infateunlandable", $"mounted+stationary in fate >{FateStuckWindowMs}ms -> new landable point {_fateDropoff}");
                    return;
                }

                Navigator.Stop();
                Features.MountManager.Dismount();
                StatusText = $"Landing/dismounting before engaging: {fate.Name}";
                return;
            }
            _inFateMountMs = 0; // dialogue up -> not the stuck case
        }
        else
        {
            _inFateMountMs = 0; // DISMOUNTED in the fate => we made it. Clear the recovery sampler.
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

        // POST-INTERACT HOLD: after we've fired the interact on the start NPC, the Talk addon takes
        // a few ticks to actually appear. During that window `dialogueOpen` is still false and the
        // start latch is already set, so without this guard we'd fall through to Navigator.MoveTo
        // below — and MOVING cancels the pending interaction ("event canceled", dialogue flickers
        // open then vanishes). So: once we've interacted, STAND STILL until the dialogue opens or
        // the fate actually starts (or a timeout), letting TryStartFateViaNpc drive it next tick.
        if (_fateNpcInteractedMs != 0
            && _startedFateId == _targetFateId
            && fate.Progress <= 0
            && fate.State != FateState.Running
            && Environment.TickCount64 - _fateNpcInteractedMs < 5000)
        {
            Navigator.Stop();
            StatusText = $"Starting fate: {fate.Name} (waiting for dialogue)";
            return;
        }

        // ENEMY NAV HAS ABSOLUTE PRIORITY once inside a fate. If there's any fate enemy to fight
        // (a fresh nearest one OR our sticky engaged target), DO NOT re-center to the ring point —
        // EnsureCombatEngaged owns all movement and walks us to the enemy, however far. Re-centering
        // here is what rubber-banded us back to the middle mid-chase. We only re-center to find
        // action when there is genuinely nothing to fight.
        var hasFateEnemy = FateTargeting.GetNearestFateEnemy(_targetFateId) != null
            || (_engagedTargetId != 0
                && Svc.Objects.SearchById(_engagedTargetId) is IBattleNpc eng
                && FateTargeting.IsFateEnemy(eng, _targetFateId));
        if (!BmrMovementActive() && !hasFateEnemy && !IsInsideFate(fate) && !ECommons.GenericHelpers.IsOccupied())
            Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.6f), allowMount: false);

        // ESCORT DETECTION by RING MOVEMENT. An escort/follow fate's ring center (fate.Position)
        // MOVES as the escorted NPC walks its route; a normal fate's ring is fixed. The sheet Rule
        // is unreliable ("The Ceruleum Road" is a follow fate but classifies as Battle/Rule=3), and
        // MotivationNpc is NOT a follow signal (many non-escort fates expose one — e.g. the
        // Twin-tongued Addison fate). So we detect by watching the ring drift:
        //   - sample the ring center the first time we see this fate;
        //   - once it has moved past RingMoveFollowThreshold, latch this fate as a follow fate.
        // A fate explicitly classified Escort is always a follow fate immediately.
        if (!_fatePosSampled)
        {
            _fateInitialPos = fate.Position;
            _fatePosSampled = true;
        }
        else if (!_ringMovedFollow && Vector3.Distance(_fateInitialPos, fate.Position) >= RingMoveFollowThreshold)
        {
            _ringMovedFollow = true;
            Diag("Escort", "ringmove", $"ring moved {Vector3.Distance(_fateInitialPos, fate.Position):F1}y -> follow fate {_targetFateId}");
        }

        var isFollowFate = type == FateType.Escort || _ringMovedFollow;

        switch (type)
        {
            case FateType.Collect:
                HandleCollectFate(fate);
                break;
            default:
                if (isFollowFate) { HandleEscortFate(fate); break; }
                // Battle / Boss / Defend: combat backend does the work. We just make sure we have a target.
                EnsureCombatEngaged(fate);
                break;
        }

        // Fate completed by progress.
        if (fate.Progress >= 100)
            OnFateFinished();
    }

    // The fate enemy we've committed to killing. STICKY: we keep this target until it dies or
    // becomes invalid (this is the single-source-of-truth that the reference AutoTarget uses — its
    // default retarget rule is "only switch if you have no target / are targeting an ally"). The old
    // code re-picked the target in three different places with three different rules every tick,
    // which is exactly why it flickered between targets and sometimes stood still pointing at one.
    private ulong _engagedTargetId;
    // Sticky mass-pull body-pull target: the un-aggroed mob we're currently walking to in order to
    // pull its aggro. Kept until it's on us / dies / leaves, so the move target can't oscillate.
    private ulong _massPullTargetId;

    private void EnsureCombatEngaged(IFate fate)
    {
        var me = Player.Object;
        if (me == null) return;

        // GROUNDED GUARD: never target / engage while mounted or in the air. The combat backend
        // can't act while mounted, and targeting mid-flight makes us dive at mobs from above. Land
        // and dismount FIRST, then bail this tick — we re-evaluate targeting once we're on foot.
        if (Features.MountManager.IsMounted || Features.MountManager.IsFlying)
        {
            Diag("Combat", "grounded", $"mounted/flying -> dismounting before engage (mounted={Features.MountManager.IsMounted} flying={Features.MountManager.IsFlying})");
            Navigator.Stop();
            Features.MountManager.Dismount();
            StatusText = $"Landing/dismounting before engaging: {fate.Name}";
            return;
        }

        // BMR AOE-DODGE YIELD — ONLY while actually IN COMBAT. This latch hands movement to BMR so
        // vnav and BMR don't fight while dodging. But it must NEVER block us from APPROACHING mobs:
        // a forbidden zone (e.g. a fire on the ground) sets DangerPresent()=true even when we're far
        // away and out of combat, and gating on it there froze us in place staring at distant mobs
        // (the recurring "stuck not navigating" bug). So we only yield once we're engaged.
        if (BmrMovementActive() && InCombat())
        {
            var now = Environment.TickCount64;
            if (IPCManager.YieldMovementForDodge() || IPCManager.DangerPresent())
            {
                Diag("Combat", "yield", $"yielding movement to BMR (dodge={IPCManager.YieldMovementForDodge()} danger={IPCManager.DangerPresent()})");
                _yieldUntilMs = now + YieldSettleMs;
                Navigator.Stop();
                return;
            }
            if (now < _yieldUntilMs)
            {
                Diag("Combat", "yield", $"in post-dodge settle window ({_yieldUntilMs - now}ms left)");
                Navigator.Stop();
                return;
            }
        }

        // ---- SINGLE TARGET SELECTION (sticky) --------------------------------------------------
        // Exactly ONE place picks the combat target, and it is sticky: keep the current engaged
        // target while it's still a live enemy in OUR fate. Only re-pick when it's gone/invalid.
        //
        var combatTarget = SelectCombatTarget(fate);

        Diag("Combat", "select", $"selected={(combatTarget?.Name.ToString() ?? "<null>")} engagedId={_engagedTargetId} fateEnemies={FateTargeting.CountFateEnemies(_targetFateId)} fateId={_targetFateId} curTarget={(Svc.Targets.Target?.Name.ToString() ?? "<null>")}");

        if (combatTarget == null)
        {
            // No fate mobs right now. Drift toward the fate centre to find action (unless BMR drives
            // movement, in which case it'll reposition us itself).
            _engagedTargetId = 0;
            _massPullTargetId = 0;
            Diag("Combat", "notarget", $"no fate enemy; driftToCenter={(!BmrMovementActive() && Vector3.Distance(me.Position, fate.Position) > fate.Radius * 0.4f)} distToCenter={Vector3.Distance(me.Position, fate.Position):F1} radius={fate.Radius:F1}");
            if (!BmrMovementActive() && Vector3.Distance(me.Position, fate.Position) > fate.Radius * 0.4f)
                Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.4f), allowMount: false);
            StatusText = $"In fate: {fate.Name} (waiting for mobs)";
            return;
        }

        // Commit the target ONCE (only write Svc.Targets.Target when it actually changes — redundant
        // writes every tick are part of what made the game UI flicker).
        _engagedTargetId = combatTarget.GameObjectId;
        if (Svc.Targets.Target is not IBattleNpc cur || cur.GameObjectId != combatTarget.GameObjectId)
            Svc.Targets.Target = combatTarget;

        // ---- MOVE TARGET (may differ from combat target for mass-pull body-pulling) ------------
        // The thing we WALK to. Normally the combat target. For mass pull, while under the pile cap,
        // we walk to the nearest un-aggroed fate mob to body-pull it — WITHOUT changing the combat
        // target (so the rotation keeps killing one mob while we gather more). Decoupling these is
        // what lets us "pull n, aoe, repeat" without the target thrashing.
        var moveTarget = combatTarget;
        if (C.MassPull)
        {
            var aggroed = FateTargeting.CountAggroedFateEnemies(_targetFateId);
            if (aggroed < C.MassPullMaxPile)
            {
                // STICKY pull target: keep walking to the SAME un-aggroed mob until it's actually on
                // us (or dies/leaves), then pick the next. Re-picking nearest every tick would thrash
                // when two candidates are similar distance. Sticky here mirrors the sticky combat
                // target and is the other half of the anti-oscillation fix.
                IBattleNpc? pull = null;
                if (_massPullTargetId != 0
                    && Svc.Objects.SearchById(_massPullTargetId) is IBattleNpc cand
                    && FateTargeting.IsFateEnemy(cand, _targetFateId)
                    && !FateTargeting.IsAggroedOnUs(cand, me.GameObjectId, FateTargeting.GetChocoboId()))
                {
                    pull = cand; // still valid + not yet pulled -> keep going for it
                }
                else
                {
                    pull = FateTargeting.GetNearestUnaggroedFateEnemy(_targetFateId);
                    _massPullTargetId = pull?.GameObjectId ?? 0;
                }
                if (pull != null) moveTarget = pull;
            }
            else _massPullTargetId = 0; // pile full -> stop body-pulling
        }
        else _massPullTargetId = 0;

        // ---- MOVEMENT --------------------------------------------------------------------------
        // ALWAYS path to the move target when out of range — fate enemies must be reached at ANY
        // distance. No IsOccupied / throttle gates here (those caused the "targets a mob but won't
        // walk to it" stall).
        var dist = Vector3.Distance(me.Position, moveTarget.Position);
        var engageRange = Math.Max(2.5f, moveTarget.HitboxRadius + 2.5f);

        Diag("Combat", "move", $"target='{combatTarget.Name}' move='{moveTarget.Name}' dist={dist:F1} engageRange={engageRange:F1} outOfRange={dist > engageRange} bmrMove={BmrMovementActive()} inCombat={InCombat()} navRunning={Autofate.IPC.NavmeshIPC.IsRunning()} navPathing={Autofate.IPC.NavmeshIPC.PathfindInProgress()} meshReady={Autofate.IPC.NavmeshIPC.MeshReady()} myPos={me.Position} targetPos={moveTarget.Position}");

        if (dist > engageRange)
        {
            // OUT OF RANGE.
            if (BmrMovementActive())
            {
                // CRITICAL (AOE-dodge fix): only steal movement from BMR to APPROACH while we're NOT
                // already in combat. BMR won't chase an un-aggroed mob, so we vnav in to start the
                // pull — but the moment we're in combat, BMR both chases the aggroed target AND
                // dodges AOEs. If we keep yanking movement back based on distance, the instant BMR
                // steps us out of an AOE we'd be "out of range" and vnav would drag us right back in,
                // looping us back and forth at engage range instead of dodging. So once in combat we
                // hand movement fully to BMR and do NOT path ourselves.
                if (InCombat())
                {
                    IPCManager.SetBmrMovement(true);  // BMR owns approach + dodge while fighting
                    Navigator.Stop();
                    StatusText = $"Fighting {combatTarget.Name} (BMR repositioning)";
                }
                else
                {
                    IPCManager.SetBmrMovement(false); // not in combat yet -> vnav closes in to pull
                    Navigator.MoveTo(C, moveTarget.Position, engageRange, allowMount: false);
                    StatusText = $"Engaging {combatTarget.Name} (moving to {moveTarget.Name})";
                }
            }
            else
            {
                Navigator.MoveTo(C, moveTarget.Position, engageRange, allowMount: false);
                StatusText = $"Engaging {combatTarget.Name} (moving to {moveTarget.Name})";
            }
        }
        else
        {
            if (BmrMovementActive())
                IPCManager.SetBmrMovement(true); // in range -> hand movement back so BMR dodges/fights
            else
                Navigator.Stop();
            // Open combat ourselves so the backend engages even if it's waiting to be hit.
            FateTargeting.StartAutoAttack(combatTarget);
            StatusText = $"Fighting {combatTarget.Name}";
        }
    }

    /// <summary>
    /// Pick the fate combat target, STICKILY (mirrors the reference AutoTarget retarget rule). Order:
    ///   1) Keep the currently engaged target if it's still a live enemy in OUR fate.
    ///   2) Otherwise, for Defend/Escort fates, prefer the enemy attacking a protected friendly.
    ///   3) Otherwise the nearest fate enemy.
    /// Returns null when our fate has no live enemies. This single selector replaces the three
    /// conflicting ones the old code ran each tick.
    /// </summary>
    private IBattleNpc? SelectCombatTarget(IFate fate)
    {
        // 1) STICKY: keep the engaged target while it's valid (alive + in our fate). This is the
        //    anti-flicker rule — we do NOT yank to a closer mob just because one wandered nearer.
        if (_engagedTargetId != 0
            && Svc.Objects.SearchById(_engagedTargetId) is IBattleNpc engaged
            && FateTargeting.IsFateEnemy(engaged, _targetFateId))
        {
            return engaged;
        }

        // 2) Defend/Escort peel: the enemy actively attacking a protected friendly takes priority
        //    when we don't already have a valid target.
        var type = FateSelector.Classify(fate);
        if (type == FateType.Defend || type == FateType.Escort)
        {
            var threat = FateTargeting.GetActiveDefendThreat(_targetFateId);
            if (threat != null) return threat;
        }

        // 3) Nearest fate enemy.
        return FateTargeting.GetNearestFateEnemy(_targetFateId);
    }

    private bool BmrMovementActive() => IPCManager.BmrHandlesMovement(C);

    // ---------------------------------------------------------------- collect fates
    // Collect fates: grab labeled ground items (EventObj carrying the FateId), hand them in to the
    // game-designated objective NPC in fixed batches, and leave once the fate hits 100% (or runs dry
    // of items with progress still incomplete). We don't try to LEARN a per-item progress value
    // (that was the old, never-calibrated WIP path); instead we mirror DWD/BMR: hold until we have a
    // full batch, hand in, repeat — with a final partial hand-in when no more items remain.
    // Collect-fate goal, computed fresh each tick (mirrors DWD's FateUtils.GetGoal). EXACTLY ONE of
    // these drives the tick — that single-owner model is what stops the target tug-of-war that made
    // us flicker between the NPC and an enemy.
    private enum CollectGoal { None, HandIn, Pickup, Fight }

    private CollectGoal GetCollectGoal(IFate fate, uint collectItemId, int have)
    {
        // Nothing to do once the fate is done.
        if (fate.Progress >= 100) return CollectGoal.None;
        if (collectItemId == 0) return CollectGoal.None;

        var moreItemsOnGround = FateTargeting.GetNearestCollectable(_targetFateId) != null;

        // HAND IN a FULL batch first (efficient, contributes to the shared bar).
        if (have >= CollectBatchSize)
            return CollectGoal.HandIn;

        // FIGHT if there are enemies in OUR fate. Others may have already started it and the enemies
        // ARE the work (they drop the collectables) — going to fight them beats trekking to the start
        // NPC or idling. Also covers being kept in combat. FateId-scoped: never foreign-fate mobs.
        if (FateTargeting.GetNearestFateEnemy(_targetFateId) != null || InCombat())
            return CollectGoal.Fight;

        // PICK UP the nearest ground collectable (no enemies around).
        if (moreItemsOnGround)
            return CollectGoal.Pickup;

        // Nothing left to fight or grab but we still hold items -> hand in the partial batch.
        if (have > 0)
            return CollectGoal.HandIn;

        return CollectGoal.None;
    }

    private unsafe void HandleCollectFate(IFate fate)
    {
        // SHARED GOAL: collect fates progress for EVERYONE — anyone turning in items advances the
        // same bar. This method runs every tick, so re-check completion FIRST: the moment progress
        // hits 100 (whether from us or other players) stop and leave instead of doing more work.
        // OnFateFinished routes through ClearingAggro if we're still in combat.
        if (fate.Progress >= 100) { OnFateFinished(); return; }

        var collectItemId = FateSelector.GetCollectItemId(fate);
        var have = collectItemId != 0 ? InventoryUtil.GetItemCount(collectItemId) : 0;

        // TURN-IN DELEGATION (the reference plugins' approach): TextAdvance handles the ENTIRE
        // Request-window flow (fills the slot + clicks Hand Over + skips the Talk dialogue). Our old
        // manual AgentInventoryContext.OpenForItemSlot poke is what made the inventory "pull up then
        // instantly close" — the game discards that synthetic context menu. We just open the turn-in
        // dialogue and let TextAdvance complete it.
        //
        // RE-ASSERT control here every tick (self-healing). TextAdvance can silently drop external
        // control (zone change / its own timeout / reload), and if it has, the Request window sits
        // unfilled forever — the "stuck at turn-in until I restart the plugin" bug. Enable() reconciles
        // against TextAdvance's ACTUAL state and re-issues the request when it isn't in control.
        TextAdvanceIPC.Enable();

        // If the Request window is open, TextAdvance is handling it — keep the combat backend OFF so
        // nothing competes, and just wait for it to finish.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "Request", out var req) && req != null && req->IsVisible)
        {
            SetCombatBackend(false);
            Diag("Collect", "request", $"Request window open: taInstalled={TextAdvanceIPC.IsInstalled} taControl={TextAdvanceIPC.IsInExternalControl()} item={collectItemId} have={have}");
            // Only trust TextAdvance to drive it if it's ACTUALLY in external control right now. If
            // it isn't (not installed, or dropped control and somehow didn't re-take), fall back to
            // driving the Request window ourselves so we never get stuck with the slot unfilled.
            if (TextAdvanceIPC.IsInstalled && TextAdvanceIPC.IsInExternalControl())
            {
                StatusText = "Collect: handing in (TextAdvance)";
                return;
            }
            HandleRequestWindow((nint)req, fate, collectItemId); // fallback: fill + hand over ourselves
            return;
        }

        var goal = GetCollectGoal(fate, collectItemId, have);
        Diag("Collect", "goal", $"goal={goal} item={collectItemId} have={have} progress={fate.Progress} groundItems={(FateTargeting.GetNearestCollectable(_targetFateId) != null)} inCombat={InCombat()}");

        // SINGLE-OWNER RULE: for every goal EXCEPT Fight, the rotation backend must be OFF. If it's
        // running it will re-target an enemy every frame and fight us for Svc.Targets.Target — that
        // is the "flicks between the NPC and an enemy without doing anything" bug. We only turn the
        // backend on for the Fight goal (something is actively attacking us).
        SetCombatBackend(goal == CollectGoal.Fight);

        switch (goal)
        {
            case CollectGoal.HandIn:
                CollectHandIn(fate, have);
                return;

            case CollectGoal.Fight:
                // Something is attacking us — let the combat path clear it, then we resume next tick.
                EnsureCombatEngaged(fate);
                StatusText = "Collect: clearing combat before gathering";
                return;

            case CollectGoal.Pickup:
                CollectPickup(have);
                return;

            default: // None
                // Fate done, or waiting for items to respawn / combat to clear. Don't touch the
                // target (no flicker); just idle near the centre so we're positioned for the next item.
                if (fate.Progress >= 100) { OnFateFinished(); return; }
                StatusText = $"Collect fate: {fate.Name} {fate.Progress}% (have {have})";
                if (!BmrMovementActive())
                    Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.5f), allowMount: false);
                return;
        }
    }

    /// <summary>Walk to the objective NPC and interact to open the turn-in window. Combat backend is
    /// already OFF (caller guarantees), so nothing competes for the target.</summary>
    private unsafe void CollectHandIn(IFate fate, int have)
    {
        var npc = FateTargeting.GetCollectTurnInNpc(_targetFateId);
        if (npc == null)
        {
            StatusText = "Collect: looking for turn-in NPC";
            return;
        }

        var me = Player.Object;
        if (me == null) return;

        if (Vector3.Distance(me.Position, npc.Position) > 4f)
        {
            Navigator.MoveTo(C, npc.Position, 3f, allowMount: false);
            StatusText = $"Collect: turning in {have} (to {npc.Name})";
            return;
        }

        // Set the NPC target ONCE and keep it (backend is off, so it stays put — no flicker).
        if (Svc.Targets.Target?.GameObjectId != npc.GameObjectId)
            Svc.Targets.Target = npc;
        TryCollectInteract(npc, $"Collect: interacting with {npc.Name}");
    }

    /// <summary>Walk to the nearest ground collectable and interact to pick it up. Combat backend is
    /// already OFF (caller guarantees), so nothing competes for the target.</summary>
    private unsafe void CollectPickup(int have)
    {
        var item = FateTargeting.GetNearestCollectable(_targetFateId);
        if (item == null) return;

        var me = Player.Object;
        if (me == null) return;

        if (Vector3.Distance(me.Position, item.Position) > 3f)
        {
            Navigator.MoveTo(C, item.Position, 2f, allowMount: false);
            StatusText = $"Collect: grabbing {item.Name} (have {have})";
            return;
        }

        if (Svc.Targets.Target?.GameObjectId != item.GameObjectId)
            Svc.Targets.Target = item;
        TryCollectInteract(item, $"Collect: grabbing {item.Name} (have {have})");
    }

    /// <summary>
    /// Fire a collect interact (NPC turn-in or ground pickup) the way the reference executor does:
    ///   - STOP moving first (interacting while still pathing cancels the in-flight interact);
    ///   - only when NOT animation-locked and interactable;
    ///   - and ONLY ONCE per object until a cooldown expires, because the addon/cast it triggers
    ///     opens on a server roundtrip and isn't visible next tick. Re-firing in that gap is what
    ///     cancelled the opening Request window and made it open/close in an infinite loop.
    /// </summary>
    private unsafe void TryCollectInteract(IGameObject obj, string status)
    {
        Navigator.Stop(); // critical: never interact while still moving toward the target
        if (Player.IsAnimationLocked || !Player.Interactable)
        {
            Diag("Collect", "interact", $"blocked (animLock={Player.IsAnimationLocked} interactable={Player.Interactable}) obj='{obj.Name}'");
            return;
        }

        var now = Environment.TickCount64;
        // Still within the cooldown for THIS object -> the previous interact is resolving; wait.
        if (_collectInteractObjId == obj.GameObjectId && now < _collectInteractCooldownMs)
        {
            Diag("Collect", "interact", $"cooldown ({_collectInteractCooldownMs - now}ms left) obj='{obj.Name}'");
            StatusText = status + " (waiting)";
            return;
        }

        Diag("Collect", "interact", $"FIRING InteractWithObject on '{obj.Name}' (id={obj.GameObjectId})");
        FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()
            ->InteractWithObject(((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address), false);
        _collectInteractObjId = obj.GameObjectId;
        _collectInteractCooldownMs = now + CollectInteractCooldownMs;
        StatusText = status;
    }

    /// <summary>
    /// Drive the Item Request (collect turn-in) window: fill the slot with the collect item, then
    /// click Hand Over. We use ECommons' Request master for Hand Over + AgentInventoryContext to
    /// fill the slot (right-click the inventory item against the open Request addon).
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

        // CROSS-FATE TARGET LOCK. This is the fix for "we start attacking another fate's mobs when
        // the escort walks through it." Two compounding causes:
        //   1) Our own target pick is FateId-scoped (good), but the rotation backend (Wrath/RSR/BMR)
        //      re-targets on its OWN, and a pass-through fate's nearby mobs are valid hostiles to it.
        //   2) A pass-through fate's mob can land a hit on us, pulling us into ITS fight.
        // Defence: pick a threat scoped to OUR fate only, then RE-ASSERT it every tick. If the
        // backend has yanked the target onto anything that is NOT one of our fate's enemies, we
        // forcibly take it back (or clear it) so the rotation can't keep hitting the foreign mob.
        var threat = FateTargeting.GetActiveDefendThreat(_targetFateId)
                     ?? FateTargeting.GetNearestFateEnemy(_targetFateId);

        var curTarget = Svc.Targets.Target as IBattleNpc;
        var curIsOurFateEnemy = curTarget != null && FateTargeting.IsFateEnemy(curTarget, _targetFateId);
        if (threat != null)
        {
            // Re-assert every tick (not just on change): the backend may have switched to a foreign
            // mob since last tick. Only keep the current target if it's already one of OUR fate's
            // enemies (don't yank a valid in-fate target mid-cast).
            if (!curIsOurFateEnemy)
                Svc.Targets.Target = threat;
            FateTargeting.StartAutoAttack(threat);
        }
        else if (curTarget != null && !curIsOurFateEnemy)
        {
            // No threat in OUR fate, but the backend has locked onto something foreign (e.g. a
            // pass-through fate's mob). Clear it so the rotation stops attacking it — we just want
            // to keep escorting.
            Svc.Targets.Target = null;
        }

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
            // Prefer the game-designated MotivationNpc; only fall back to the nearest our-fate
            // friendly for true Escort-typed fates that somehow expose no MotivationNpc.
            var motivationId = FateTargeting.GetFateMotivationNpcId(_targetFateId);
            escortNpc = (motivationId != 0 ? Svc.Objects.FirstOrDefault(o => o.GameObjectId == motivationId) as IBattleNpc : null)
                        ?? FateTargeting.GetDefendedFriendlies(_targetFateId).FirstOrDefault();
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
            Diag("Escort", "npc", $"escort NPC unresolved/dead (cachedId={_escortNpcId}) -> holding");
            Navigator.Stop();
            return;
        }
        var followPos = escortNpc.Position;
        Diag("Escort", "follow", $"npc='{escortNpc.Name}' id={_escortNpcId} threat={(threat?.Name.ToString() ?? "<null>")} curTarget={(curTarget?.Name.ToString() ?? "<null>")} curIsOurFate={curIsOurFateEnemy} distToNpc={Vector3.Distance(Player.Object?.Position ?? followPos, followPos):F1}");

        // MOVEMENT: WE own movement for the entire escort. The old code toggled BMR movement on/off
        // every tick based on distance — that made the character chase a mob (BMR) then get yanked
        // back to the NPC (us), flapping back and forth ("walking away and back over and over").
        // Instead: forbid BMR movement ONCE (set-guarded so no chat spam) and always drive movement
        // ourselves. BMR still fights/dodges in place; the rotation kills our target.
        if (BmrMovementActive())
            IPCManager.SetBmrMovement(false); // change-guarded internally; only fires once

        var me = Player.Object;
        if (me == null) { Navigator.Stop(); return; }

        // KILL-ANY-ENEMY mode: we don't body-pull mobs back to the NPC. If OUR fate has any enemy,
        // go kill it wherever it is (no distance limit), then once they're all dead navigate back to
        // the escort NPC. `threat` was already picked + re-asserted above (FateId-scoped), so just
        // navigate into range of it and let the rotation kill it.
        if (threat != null)
        {
            _escortChasing = false; // not in NPC-follow mode while fighting
            var distToMob = Vector3.Distance(me.Position, threat.Position);
            var engage = Math.Max(2.5f, threat.HitboxRadius + 2.5f);
            if (distToMob > engage)
            {
                Navigator.FollowMoveTo(C, threat.Position, engage);
                StatusText = $"Escort: killing {threat.Name}";
            }
            else
            {
                Navigator.Stop(); // in range — stand and let the rotation finish it
                StatusText = $"Escort: fighting {threat.Name}";
            }
            return;
        }

        // NO ENEMIES LEFT: navigate back to / follow the escort NPC. Hysteresis so we don't flap:
        // start chasing past EscortNpcGlueStart, stop once within EscortNpcGlueStop. FollowMoveTo
        // re-issues as the NPC drifts.
        var distToNpc = Vector3.Distance(me.Position, followPos);
        if (_escortChasing)
        {
            if (distToNpc <= EscortNpcGlueStop) { _escortChasing = false; Navigator.Stop(); }
            else Navigator.FollowMoveTo(C, followPos, EscortNpcGlueStop);
        }
        else
        {
            if (distToNpc > EscortNpcGlueStart) { _escortChasing = true; Navigator.FollowMoveTo(C, followPos, EscortNpcGlueStop); }
            else Navigator.Stop();
        }
    }

    private const float EscortNpcGlueStart = 5f;
    private const float EscortNpcGlueStop = 2.5f;

    /// <summary>
    /// Clear per-fate carry-over state. CRITICAL: escort/collect state (especially the cached escort
    /// NPC id) must be wiped whenever we move to a NEW fate, not only via OnFateFinished — an escort
    /// fate can end WITHOUT OnFateFinished (expired, abandoned, completed by other players). If the
    /// stale _escortNpcId leaked into the next fate, hasEscortNpc stayed true and we'd run the escort
    /// handler forever on a non-escort fate, holding while "looking for" a dead NPC.
    /// </summary>
    private void ResetPerFateState()
    {
        _escortNpcId = 0;
        _escortChasing = false;
        _engagedTargetId = 0;
        _massPullTargetId = 0;
        _collectInteractObjId = 0;
        _collectInteractCooldownMs = 0;
        _yieldUntilMs = 0;
        _fatePosSampled = false;
        _ringMovedFollow = false;
        _fateDropoff = null;
        _fateStuckLastSampleMs = 0;
        _groundStuckLastMs = 0;
        _groundJumpPhase = 0;
        _groundNudgeTarget = null;
        _inFateMountMs = 0;
    }

    private void OnFateFinished()
    {
        Stats.OnFateCompleted();
        Features.ChocoboManager.CheckXpGainAfterFate(); // detect rank-cap stall (no XP gained this fate)
        Navigator.Stop();
        _yieldUntilMs = 0; // clear the smart-mix yield latch
        _engagedTargetId = 0; // drop the sticky combat target so the next fate re-selects fresh
        _massPullTargetId = 0; // drop the sticky body-pull target too
        TextAdvanceIPC.Disable(); // release collect turn-in control (no-op if we never took it)

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
        _fateNpcInteractedMs = 0;
        _fateStartConfirmedMs = 0;
        _postFateGraceUntilMs = Environment.TickCount64 + PostFateGraceMs; // wait for a chained spawn
        ResetPerFateState();
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
    /// <summary>
    /// While grounded + navigating, if we barely move for >2s we're blocked by geometry. Back out
    /// ~5y (opposite our facing), then force the path to regenerate. Returns true if it consumed the
    /// tick (caller must return so nothing else issues movement this frame).
    /// </summary>
    private bool TickGroundedStuck()
    {
        var me = Player.Object;
        if (me == null) return false;

        // Drive an in-progress back-out nudge to completion before anything else moves us.
        if (_groundNudgeTarget is { } back)
        {
            if (Vector3.Distance(me.Position, back) <= 2f || Environment.TickCount64 >= _groundNudgeUntilMs)
            {
                _groundNudgeTarget = null;
                Navigator.Stop();                 // clears last dest -> next MoveTo regenerates the path
                _groundStuckLastMs = 0;           // restart the stuck sampler
                _groundJumpPhase = 0;
                return true;
            }
            Autofate.IPC.NavmeshIPC.MoveTo(new List<Vector3> { back }, _groundNudgeFly);
            StatusText = "Unblocking (backing out)";
            return true;
        }

        // Watch while navigating on foot OR flying (mounted-ground doesn't get wedged the same way).
        var flying = Features.MountManager.IsFlying;
        if (!Navigator.IsNavigating || Player.IsJumping
            || (Features.MountManager.IsMounted && !flying))
        {
            _groundStuckLastMs = 0;
            return false;
        }

        // FLYING + STUCK: vnav can think we can move forward into a wall (e.g. inside a cave) and
        // keeps regenerating the path into it. Skip the jump phase (useless airborne) and go STRAIGHT
        // to the back-out + renav: reverse ~5y along our facing (in 3D, fly) then regenerate.
        if (flying)
        {
            var now2 = Environment.TickCount64;
            if (_groundStuckLastMs == 0) { _groundStuckLastMs = now2; _groundStuckLastPos = me.Position; return false; }
            if (now2 - _groundStuckLastMs < GroundStuckWindowMs) return false;
            var moved2 = Vector3.Distance(me.Position, _groundStuckLastPos);
            _groundStuckLastMs = now2;
            _groundStuckLastPos = me.Position;
            if (moved2 >= GroundStuckMinMove) return false; // moving fine

            var rotF = me.Rotation;
            var behindF = new Vector3(-MathF.Sin(rotF), 0f, -MathF.Cos(rotF)) * 5f;
            _groundNudgeTarget = me.Position + behindF;
            _groundNudgeFly = true;
            _groundNudgeUntilMs = now2 + 2000;
            Navigator.Stop();
            Autofate.IPC.NavmeshIPC.MoveTo(new List<Vector3> { _groundNudgeTarget.Value }, true);
            Diag("Movement", "flystuck", $"flying & moved {moved2:F1}y -> backing out 5y then repath");
            return true;
        }

        var now = Environment.TickCount64;
        if (_groundStuckLastMs == 0) { _groundStuckLastMs = now; _groundStuckLastPos = me.Position; return false; }
        if (now - _groundStuckLastMs < GroundStuckWindowMs) return false;

        var moved = Vector3.Distance(me.Position, _groundStuckLastPos);
        _groundStuckLastMs = now;
        _groundStuckLastPos = me.Position;
        if (moved >= GroundStuckMinMove) { _groundJumpPhase = 0; return false; } // moving fine

        // JUMP PHASE FIRST: jump, wait for grounded, jump again, wait the SAME window. Only if we
        // STILL haven't moved after that do we back out + renav. A single hop often clears a small
        // lip/step without the heavier back-out maneuver.
        unsafe void Jump() => FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance()
            ->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 2 /* Jump */);

        if (_groundJumpPhase == 0)
        {
            _groundJumpStartPos = me.Position;
            Navigator.Stop();
            Jump();
            _groundJumpPhase = 1; // wait for grounded after the first jump
            Diag("Movement", "groundstuck", $"moved {moved:F1}y -> jump 1");
            return true;
        }
        if (_groundJumpPhase == 1)
        {
            if (Player.IsJumping) return true;     // still airborne from jump 1
            Jump();
            _groundJumpPhase = 2;
            _groundJumpWaitUntilMs = now + GroundStuckWindowMs; // wait the same window after jump 2
            Diag("Movement", "groundstuck", "grounded -> jump 2, waiting window");
            return true;
        }
        // phase 2: waiting the window after the second jump.
        if (Player.IsJumping || now < _groundJumpWaitUntilMs) return true;
        _groundJumpPhase = 0;
        if (Vector3.Distance(me.Position, _groundJumpStartPos) >= GroundStuckMinMove)
        {
            // The jumps freed us. Resume normal navigation, restart the sampler.
            _groundStuckLastMs = 0;
            Diag("Movement", "groundstuck", "jumps cleared the block -> resuming nav");
            return true;
        }

        // Still wedged after the jumps -> back straight out ~5y (FFXIV yaw: forward = (sin,0,cos)).
        var rot = me.Rotation;
        var behind = new Vector3(-MathF.Sin(rot), 0f, -MathF.Cos(rot)) * 5f;
        _groundNudgeTarget = me.Position + behind;
        _groundNudgeFly = false;
        _groundNudgeUntilMs = now + 2000;
        Navigator.Stop();
        Autofate.IPC.NavmeshIPC.MoveTo(new List<Vector3> { _groundNudgeTarget.Value }, false);
        Diag("Movement", "groundstuck", "jumps failed -> backing out 5y then repath");
        return true;
    }

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

        // ONLY fight enemies ACTUALLY attacking us (or our chocobo). Never target a non-fate enemy
        // that isn't aggro'd on us - no GetNearestHostile fallback. If nothing is on us, we're done,
        // even if the in-combat flag is still lingering.
        var attackers = FateTargeting.GetEnemiesAttackingMe();
        var hostile = attackers.Count > 0 ? attackers[0] : null;

        Diag("Combat", "clearaggro", $"attackers={attackers.Count} hostile={(hostile?.Name.ToString() ?? "<null>")} inCombat={InCombat()}");

        if (hostile == null)
        {
            Navigator.Stop();
            SetCombatBackend(false);
            State = FarmState.SelectingFate;
            return;
        }

        var attacker = hostile;
        if (!(Svc.Targets.Target is IBattleNpc cur && FateTargeting.IsAttackableEnemy(cur)
              && (cur.GameObjectId == attacker.GameObjectId)))
            Svc.Targets.Target = attacker;

        StatusText = $"Clearing stray aggro: {attacker.Name}";
        SetCombatBackend(true); // make the rotation backend fight it

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
        if (fate != null)
        {
            Navigator.Stop();
            _targetFateId = fate.FateId;
            _startedFateId = 0;
            _fateNpcInteractedMs = 0;
            ResetPerFateState(); // clear escort/collect carry-over from any previous fate
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
            // The stable is a CUSTOM menu flow we drive ourselves (SelectString "Tend to my Chocobo"
            // + Fetch Yes/No). TextAdvance, enabled session-wide, would auto-handle those addons and
            // race/hijack our manual menu logic — that's what broke the fetch step. Release TA for
            // the whole stable cycle; we re-enable it when the routine finishes (TickChocoboLeveling).
            TextAdvanceIPC.Disable();
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
            EnterShopping();
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
        // The routine only returns true once the chocobo has actually been FETCHED back out (the
        // fetch loop retries close->reinteract->Tend->Fetch->Yes until it succeeds). No possession
        // check is needed here — the stable routine doesn't finish until the fetch is done.
        if (ChocoboStableRoutine.Tick(C))
        {
            // Stable cycle done — hand dialogue back to TextAdvance for normal fate/turn-in flow.
            TextAdvanceIPC.Enable();
            if (C.StopAtChocoboMaxed && ChocoboManager.ReachedTargetLevel(C))
                Stop(StopReason.ChocoboMaxed);
            else
                State = FarmState.SelectingZone;
        }
    }

    /// <summary>Enter the gemstone-shopping state for a fresh visit (clears the per-visit latch).</summary>
    private void EnterShopping()
    {
        _stateBeforeShop = FarmState.SelectingZone;
        _shoppingDone = false; // fresh visit: allow interacting/buying again
        State = FarmState.GemstoneShopping;
    }

    /// <summary>
    /// Conclude the current shopping visit: latch DONE, record the gem count so we don't re-enter
    /// until we've farmed more, close the window if it's open, and only leave the state once the
    /// window is actually gone. While latched, the rest of TickGemstoneShopping refuses to
    /// re-interact the vendor — this is what stops the open->buy->close->reopen loop.
    /// </summary>
    private void FinishShopping()
    {
        _shoppingDone = true;
        _gemCountAfterLastShop = Features.InventoryUtil.GetGemstoneCount();
        if (GemstoneShopper.ShopOpen())
        {
            if (EzThrottler.Throttle("AF_CloseShop", 300))
                GemstoneShopper.CloseShop();
            return; // wait for it to actually close before leaving the state
        }
        State = _stateBeforeShop;
    }

    private unsafe void TickGemstoneShopping()
    {
        StatusText = "Gemstone shopping";

        // PER-VISIT DONE LATCH: once a visit is judged complete we ONLY close + leave; we never fall
        // through to re-target/re-interact the vendor (ShouldShop can still read true for continuous
        // entries, which is exactly what used to reopen the window in a loop).
        if (_shoppingDone)
        {
            FinishShopping();
            return;
        }

        // TOP-OF-TICK COMPLETION GUARD (works whether the shop is open or not): if every enabled
        // capped buy entry has reached its target item count AND there are no continuous entries,
        // we're DONE. This catches completion even before the shop opens.
        var hasContinuous = C.GemstoneBuyList.Any(e => e.Enabled && e.TargetQuantity == 0);
        if (!hasContinuous && GemstoneShopper.AllTargetsMet(C))
        {
            FinishShopping();
            return;
        }

        // If the shop addon is open, buy until done. FORCIBLE COMPLETION: the instant there's
        // nothing left we can AND want to buy (every entry capped, unaffordable, or — for continuous
        // entries — drained back to the threshold), latch DONE and leave. This check only runs while
        // the shop is OPEN (item costs are only readable then) so we don't bail mid-travel.
        if (GemstoneShopper.ShopOpen())
        {
            if (GemstoneShopper.BuyingComplete(C))
            {
                FinishShopping();
                return;
            }
            GemstoneShopper.PurchaseTick(C);
            return;
        }

        // Interacting with the vendor often pops Talk dialogue (greeting) BEFORE the shop addon
        // opens. TextAdvance (session-wide) advances it for us; only spam-click it ourselves as a
        // FALLBACK when TextAdvance isn't installed. Gate on IsAddonReady to avoid NREs.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("Talk", out var gtalk)
            && ECommons.GenericHelpers.IsAddonReady(gtalk))
        {
            if (!TextAdvanceIPC.ControlActive && EzThrottler.Throttle("AF_GemTalk", 200))
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
