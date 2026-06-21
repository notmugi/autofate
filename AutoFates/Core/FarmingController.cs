using System.Linq;
using System.Numerics;
using AutoFates.Features;
using AutoFates.IPC;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FateState = Dalamud.Game.ClientState.Fates.FateState;

namespace AutoFates.Core;

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

    // Zone dwell: when we last saw an active FATE in the current zone. We stay and wait for FATEs
    // to respawn rather than zone-hopping the instant a zone is empty.
    private long _lastFateSeenMs;
    private uint _dwellZone;

    // Collect-fate hand-in math.
    private int _collectTurnedIn;
    private int _collectEstimatedNeeded;
    private int _collectProgressBeforeTurnIn = -1; // fate.Progress sampled before a hand-in, to learn per-turnin value
    private int _collectPerItemProgress;           // learned: progress % gained per item turned in

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
            Svc.Chat.PrintError($"[AutoFates] {error}");
            return;
        }

        Running = true;
        LastStopReason = StopReason.None;
        Stats.Reset();
        _zoneRotationIndex = 0;
        _zoneFatesDone.Clear();
        _dwellZone = 0;
        _lastFateSeenMs = Environment.TickCount64;
        State = FarmState.SelectingZone;
        StatusText = "Starting...";
        IPCManager.StartCombat(C);
        Svc.Chat.Print("[AutoFates] Farming started.");
        Svc.Log.Information("[AutoFates] Started in mode " + C.Mode);
    }

    public void Stop(StopReason reason = StopReason.UserRequested)
    {
        if (!Running) return;
        Running = false;
        LastStopReason = reason;
        State = FarmState.Stopped;
        StatusText = $"Stopped ({reason})";
        IPCManager.StopCombat(C);
        Navigator.Stop();
        if (BossModIPC.IsInstalled) BossModIPC.AiEnable(false);

        Svc.Chat.Print($"[AutoFates] Farming stopped: {reason}.");
        Svc.Log.Information($"[AutoFates] Stopped: {reason}");

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
        // skip/stop logic has something to read.
        if (C.Mode == FarmingMode.SharedFates && (C.SharedFateSkipMaxed || C.StopWhenAllSharedFatesMaxed))
            Features.SharedFateTracker.EnsureData();

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
            Svc.Chat.PrintError("[AutoFates] Out of dark matter for self-repair. Stopping.");
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
                return Data.Zones.ForMode(C.Mode).Select(z => z.TerritoryId).Where(t => t != 0).ToArray();
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
                // Leveling: stay in current zone if it has fates in our level range, else use shared fates.
                return new[] { Svc.ClientState.TerritoryType };
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
                Svc.Chat.PrintError("[AutoFates] No zones configured for this mode.");
            return;
        }

        // Manual mode rotation handling.
        if (C.Mode == FarmingMode.Manual)
        {
            HandleManualZoneSelection(zones);
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

    /// <summary>
    /// Some fates require talking to a start NPC (orange "!") to begin: it pops Talk dialogue we
    /// click through, then a Yes/No to start. Returns true while we're handling that (caller should
    /// return and let it finish). Reuses ECommons addon helpers (Talk.Click / SelectYesno.Yes).
    /// </summary>
    private unsafe bool TryStartFateViaNpc(IFate fate)
    {
        // 1) If a Yes/No prompt is up, confirm it (this is the "Assist the guard?" start prompt).
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "SelectYesno", out var yn) && yn != null && yn->IsVisible)
        {
            if (EzThrottler.Throttle("AF_FateYes", 600))
                new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectYesno((nint)yn).Yes();
            return true;
        }

        // 2) If Talk dialogue is up, click through it.
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>(
                "Talk", out var talk) && talk != null && talk->IsVisible)
        {
            if (EzThrottler.Throttle("AF_FateTalk", 250))
                new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Talk((nint)talk).Click();
            return true;
        }

        // 3) No dialogue open. Look for the fate-start NPC and interact with it.
        var npc = FateTargeting.FindFateStartNpc(_targetFateId);
        if (npc == null) return false; // nothing to start -> normal combat fate

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
        }
        return true;
    }

    private void SyncToFate()
    {
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

        var type = FateSelector.Classify(fate);
        StatusText = $"In fate: {fate.Name} ({type}) {fate.Progress}%";

        // FATE-START NPC: ONLY defend/escort fates begin by talking to an NPC (orange "!"). For
        // COLLECT fates the "!" NPC is the TURN-IN npc (handled inside HandleCollectFate), so we
        // must NOT treat it as a start NPC or we loop re-opening the Item Request window.
        if (type is FateType.Defend or FateType.Escort)
        {
            if (TryStartFateViaNpc(fate)) return;
        }

        // Keep ourselves inside the fate ring if we've drifted out. Don't do this when the combat
        // backend (BMR AI) is driving movement, and never re-mount for these short hops.
        if (!BmrMovementActive() && !IsInsideFate(fate) && !ECommons.GenericHelpers.IsOccupied())
            Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.6f), allowMount: false);

        switch (type)
        {
            case FateType.Collect:
                HandleCollectFate(fate);
                break;
            case FateType.Escort:
                HandleEscortFate(fate);
                break;
            default:
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

        if (target == null)
        {
            // No fate mobs nearby. The fate may be a boss/collect/defend lull — move toward the
            // fate center to find action (unless BMR is actively repositioning us).
            if (!ECommons.GenericHelpers.IsOccupied()
                && Vector3.Distance(me.Position, fate.Position) > fate.Radius * 0.4f)
            {
                Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.4f), allowMount: false);
            }
            return;
        }

        // MASS PULL: if enabled and we don't yet have a healthy pile gathered on us, go grab more.
        // Rule: we want at least MassPullCount (5) fate enemies within MassPullGatherRange (2y) of
        // us. While under that, walk to the nearest UNgathered enemy to body-pull it into the pile.
        if (C.MassPull && !ECommons.GenericHelpers.IsOccupied())
        {
            const int wantCount = 5;
            const float gatherRange = 2f;
            var gathered = FateTargeting.CountFateEnemiesWithin(_targetFateId, gatherRange);
            if (gathered < wantCount)
            {
                var pull = FateTargeting.GetNearestUngatheredEnemy(_targetFateId, gatherRange);
                if (pull != null)
                {
                    // IMPORTANT for casters/ranged: do NOT yank the target while we're mid-cast —
                    // changing target cancels the cast. Only (re)target the pull mob when we're not
                    // casting AND our current target isn't already a valid fate enemy. This lets the
                    // rotation land casts on whatever it's on while we walk the pile together.
                    var meCasting = Player.Object is { IsCasting: true };
                    var curValid = Svc.Targets.Target is IBattleNpc cc && FateTargeting.IsAttackableEnemy(cc);
                    if (!meCasting && !curValid)
                        Svc.Targets.Target = pull;

                    // Walk toward the pull mob to body-aggro it, but don't stop the rotation: just
                    // keep moving. (vnav move + backend cast happen in parallel.)
                    if (Vector3.Distance(me.Position, pull.Position) > 1.5f)
                        Navigator.MoveTo(C, pull.Position, 1.2f, allowMount: false);
                    else
                        Navigator.Stop();
                    return;
                }
                // Nothing left ungathered (all enemies already on us, just not 5 of them) -> fight.
            }
            // We have a full enough pile -> stop running, let the backend AoE them down.
            Navigator.Stop();
            return;
        }

        // Walk into melee/casting range of the target so the rotation backend (Wrath / RSR / BMR)
        // can attack.
        var dist = Vector3.Distance(me.Position, target.Position);
        var engageRange = Math.Max(2.5f, target.HitboxRadius + 2.5f);
        if (dist > engageRange && !ECommons.GenericHelpers.IsOccupied())
            Navigator.MoveTo(C, target.Position, engageRange, allowMount: false);
        else
            Navigator.Stop();
    }

    private bool BmrMovementActive() => IPCManager.BmrHandlesMovement(C);

    // ---------------------------------------------------------------- collect fates
    private unsafe void HandleCollectFate(IFate fate)
    {
        // Collect fates: grab the labeled ground items (EventObj carrying the FateId), turn them in
        // to the "!" NPC, and learn how many we still need from the fate progress delta. We DON'T
        // need to "complete" the fate — just reach 100% progress (or run dry), then leave.
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
        var rq = new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.Request(reqAddon);

        // If hand-over is enabled, the slot is filled -> click it.
        if (rq.IsHandOverEnabled)
        {
            if (EzThrottler.Throttle("AF_HandOver", 800))
            {
                rq.HandOver();
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
        // Escort: follow the escorted NPC (do NOT run ahead), defend it. The NPC is the fate's
        // associated object; we trail it at a short distance and let combat handle threats.
        StatusText = $"Escort fate: {fate.Name} {fate.Progress}%";

        if (BmrMovementActive())
            return; // BMR follow/defend behaviour

        // Fallback: stay near the fate center (which tracks the moving escort objective).
        if (!ECommons.GenericHelpers.IsOccupied())
            Navigator.MoveTo(C, fate.Position, 4f);
    }

    private void OnFateFinished()
    {
        Stats.OnFateCompleted();
        Navigator.Stop();
        _yieldUntilMs = 0; // clear the smart-mix yield latch

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
        StatusText = "Following party leader";

        if (Svc.Party.Length == 0)
        {
            if (EzThrottler.Throttle("AF_NoParty", 8000))
                Svc.Chat.PrintError("[AutoFates] Follow-leader enabled but you are not in a party.");
            return;
        }

        // Prefer BMR's native follow if BMR handles movement.
        if (BmrMovementActive())
        {
            var leaderIdx = Svc.Party.PartyLeaderIndex;
            if (EzThrottler.Throttle("AF_BmrFollow", 5000))
                BossModIPC.AiFollow((int)leaderIdx);
            return;
        }

        // Manual follow via vnavmesh.
        var leader = GetPartyLeaderObject();
        if (leader == null) return;
        Navigator.MoveTo(C, leader.Position, C.FollowDistance);

        // If a fate is active and we're inside it, sync.
        var fate = FateSelector.GetCurrentFate();
        if (fate != null && C.AutoLevelSync) SyncToFate();
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetPartyLeaderObject()
    {
        var idx = Svc.Party.PartyLeaderIndex;
        if (idx >= Svc.Party.Length) return null;
        var member = Svc.Party[(int)idx];
        return member?.GameObject;
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

        // Gemstone shopping.
        if (GemstoneShopper.ShouldShop(C))
        {
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

        // Self-repair flow.
        if (C.RepairMode == RepairMode.SelfRepair)
        {
            if (!RepairManager.CanSelfRepair())
            {
                Stop(StopReason.OutOfDarkMatter);
                return;
            }
            ChocoboStableRoutine.RunSelfRepair(C);
            // RunSelfRepair drives a TaskManager; when durability recovers we exit.
            if (!RepairManager.NeedsRepair(C))
                State = FarmState.SelectingZone;
            return;
        }

        // NPC repair is a tuning follow-up; for now warn and continue.
        if (EzThrottler.Throttle("AF_NpcRepair", 30000))
            Svc.Chat.PrintError("[AutoFates] NPC repair not yet automated; switch to self-repair.");
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

    private void TickGemstoneShopping()
    {
        StatusText = "Gemstone shopping";

        // If the shop addon is open, purchase; otherwise we need to reach the vendor.
        if (GemstoneShopper.ShopOpen())
        {
            if (GemstoneShopper.PurchaseTick(C))
            {
                // Close the shop and resume.
                if (EzThrottler.Throttle("AF_CloseShop", 500))
                    ECommons.Automation.Chat.ExecuteCommand("/automove off"); // no-op safety
                State = FarmState.SelectingZone;
            }
            return;
        }

        // Reaching the vendor automatically is a tuning follow-up (requires vendor NPC location).
        // For now, if the shop isn't open and we can't open it, warn and resume.
        if (EzThrottler.Throttle("AF_GemVendorNav", 30000))
            Svc.Chat.Print("[AutoFates] Open the bicolor gemstone vendor to auto-purchase your buy list.");

        if (!GemstoneShopper.ShouldShop(C))
            State = FarmState.SelectingZone;
    }
}
