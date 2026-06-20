using System.Linq;
using System.Numerics;
using AutoFates.Features;
using AutoFates.IPC;
using Dalamud.Game.ClientState.Fates;
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

    // Collect-fate hand-in math.
    private int _collectTurnedIn;
    private int _collectEstimatedNeeded;

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
        ChocoboManager.Tick(C);

        // In Shared FATEs mode, keep the in-game shared-fate tracker data loaded so zone
        // skip/stop logic has something to read.
        if (C.Mode == FarmingMode.SharedFates && (C.SharedFateSkipMaxed || C.StopWhenAllSharedFatesMaxed))
            Features.SharedFateTracker.EnsureData();

        switch (State)
        {
            case FarmState.SelectingZone: TickSelectingZone(); break;
            case FarmState.TravelingToZone: TickTravelingToZone(); break;
            case FarmState.SelectingFate: TickSelectingFate(); break;
            case FarmState.TravelingToFate: TickTravelingToFate(); break;
            case FarmState.InFate: TickInFate(); break;
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

        // For fixed-list modes, prefer the current zone if it's in the list and has fates.
        if (zones.Contains(here) && FateSelector.GetCandidates(C).Count > 0)
        {
            _targetTerritory = here;
            State = FarmState.SelectingFate;
            return;
        }

        // Otherwise pick the first zone in the list (round-robin) and travel.
        _targetTerritory = zones[_zoneRotationIndex % zones.Length];
        if (_targetTerritory == here)
        {
            // We're here but no fates currently; either wait or rotate.
            if (zones.Length > 1)
            {
                _zoneRotationIndex++;
                _targetTerritory = zones[_zoneRotationIndex % zones.Length];
            }
            else
            {
                State = FarmState.SelectingFate; // single zone, just wait for fates
                return;
            }
        }
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

        StatusText = "Selecting fate";
        var best = FateSelector.PickBest(C);
        if (best == null)
        {
            // No valid fate right now. In multi-zone modes, rotate after a wait.
            if (EzThrottler.Throttle("AF_NoFate", 8000))
            {
                var zones = GetModeZones();
                if (zones.Length > 1)
                {
                    _zoneRotationIndex++;
                    State = FarmState.SelectingZone;
                }
            }
            return;
        }

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

        // ARRIVAL FIRST: once we're inside the fate ring we've arrived. We must NOT call MoveTo
        // again here, or it will re-mount us for the (still > MountDistanceThreshold) distance to
        // the fate center while the arrival logic dismounts us — causing a mount/dismount loop.
        if (IsInsideFate(fate))
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

        // Not inside the ring yet: keep navigating toward the fate center.
        StatusText = $"Traveling to fate: {fate.Name}";
        var stopRange = Math.Max(2f, fate.Radius * 0.7f);
        Navigator.MoveTo(C, fate.Position, stopRange);
    }

    private bool IsInsideFate(IFate fate)
    {
        var me = Player.Object;
        if (me == null) return false;
        return Vector3.Distance(me.Position, fate.Position) <= fate.Radius;
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
        // CRITICAL: every backend (including BMR AI) needs a TARGET to fight — none of them will
        // go hunt fate mobs on their own. We pick the nearest live mob belonging to THIS fate
        // (matched by GameObject.FateId) and feed it to the combat backend.
        if (!EzThrottler.Throttle("AF_AcquireTarget", 300)) return;

        var target = FateTargeting.EnsureFateTarget(_targetFateId);

        if (target != null && EzThrottler.Throttle("AF_TargetLog", 3000))
            Svc.Log.Debug($"[Combat] Targeting fate mob '{target.Name}' ({FateTargeting.CountFateEnemies(_targetFateId)} fate mobs nearby).");

        if (target == null)
        {
            // No fate mobs nearby. If there are none anywhere in the ring, the fate may be a
            // boss/collect/defend lull — move toward the fate center to find action.
            if (!BmrMovementActive() && !ECommons.GenericHelpers.IsOccupied()
                && Vector3.Distance(Player.Object!.Position, fate.Position) > fate.Radius * 0.4f)
            {
                Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.4f), allowMount: false);
            }
            return;
        }

        if (BmrMovementActive())
        {
            // BMR AI handles approach + rotation + dodging once it has a target. We just keep the
            // target fresh (done above). Nothing else to do — do NOT drive movement ourselves.
            return;
        }

        // Non-BMR movement: walk into melee/casting range of the target so the rotation backend
        // (Wrath / RSR) can attack, and keep pulling additional fate mobs if mass-pull is on.
        var me = Player.Object;
        if (me == null) return;

        var dist = Vector3.Distance(me.Position, target.Position);
        var engageRange = C.MassPull ? 2.5f : Math.Max(2.5f, target.HitboxRadius + 2.5f);
        if (dist > engageRange && !ECommons.GenericHelpers.IsOccupied())
            Navigator.MoveTo(C, target.Position, engageRange, allowMount: false);
        else
            Navigator.Stop();
    }

    private bool BmrMovementActive() => IPCManager.BmrHandlesMovement(C);

    // ---------------------------------------------------------------- collect fates
    private void HandleCollectFate(IFate fate)
    {
        // Collect fates: we don't need to "complete" them, just hit the turn-in threshold.
        // Strategy: pick up items / kill mobs (handled by combat backend + looting), then turn in.
        // We estimate needed turn-ins by turning in an initial batch and using the progress delta.
        var collectItemId = FateSelector.GetCollectItemId(fate);

        // For now, keep the player engaged (grab items, fight if aggroed) via the combat backend,
        // and periodically attempt a turn-in when we have items. The precise turn-in addon flow is
        // flagged for in-game tuning.
        StatusText = $"Collect fate: {fate.Name} {fate.Progress}% (item {collectItemId})";

        if (BmrMovementActive())
            return; // BMR will move us around; manual item pickup is a tuning follow-up

        // Fallback movement: orbit the fate center.
        if (!ECommons.GenericHelpers.IsOccupied())
            Navigator.MoveTo(C, fate.Position, Math.Max(2f, fate.Radius * 0.5f));
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
        State = FarmState.SelectingFate;
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
