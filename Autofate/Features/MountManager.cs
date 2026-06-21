using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace Autofate.Features;

/// <summary>
/// Handles mounting and taking flight for long-distance travel. The user can pick a preferred
/// mount; if none (or "Mount Roulette") is set we use the Mount Roulette general action so we
/// don't need to know a specific unlocked mount.
/// </summary>
public static unsafe class MountManager
{
    private const uint MountRouletteGeneralAction = 9; // "Mount Roulette" general action
    private const uint SprintGeneralAction = 4;        // "Sprint" general action (full-duration in sanctuaries)
    private const int FlyJumpThrottleMs = 400;

    public static bool IsMounted => Player.Mounted;

    // HARD RULE: never mount in a housing district, even though the game allows it.
    public static bool CanMountHere => Player.CanMount && !InHousingDistrict;

    /// <summary>
    /// True if we're in a housing district (residential ward, plot/instance, or apartment).
    /// Housing TerritoryIntendedUse: 13 = residential ward, 14 = housing instance (private/FC
    /// plot), 60 = apartment building.
    /// </summary>
    public static bool InHousingDistrict
    {
        get
        {
            try
            {
                var terr = Svc.Data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(Svc.ClientState.TerritoryType);
                if (terr is not { } row) return false;
                var use = row.TerritoryIntendedUse.RowId;
                return use is 13 or 14 or 60;
            }
            catch { return false; }
        }
    }

    /// <summary>True if Sprint (or the sanctuary speed buff, status 50) is currently active.</summary>
    public static bool IsSprinting
    {
        get
        {
            try
            {
                var me = Player.Object as Dalamud.Game.ClientState.Objects.Types.IBattleChara;
                if (me == null) return false;
                foreach (var s in me.StatusList)
                    if (s.StatusId is 50 or 1199) return true; // 50 = Sprint, 1199 = Peloton (party sprint)
                return false;
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Use the Sprint general action (id 4). In towns/sanctuaries this grants a long-duration speed
    /// boost; elsewhere it's the short combat sprint. Throttled and skipped if already sprinting.
    /// </summary>
    public static bool Sprint()
    {
        if (IsSprinting) return true;
        if (IsMounted) return false; // sprint doesn't apply while mounted
        if (ECommons.GenericHelpers.IsOccupied()) return false;
        if (!EzThrottler.Throttle("AF_Sprint", 3000)) return false;
        try
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, SprintGeneralAction);
            return true;
        }
        catch (Exception e) { Svc.Log.Verbose($"[Mount] Sprint failed: {e.Message}"); return false; }
    }

    public static bool CanFlyHere => Player.CanFly;

    /// <summary>Summon a mount (preferred mount if owned/valid, otherwise Mount Roulette).</summary>
    public static bool Mount(Configuration c)
    {
        if (IsMounted) return true;
        if (!CanMountHere) return false;
        if (ECommons.GenericHelpers.IsOccupied()) return false;
        if (!EzThrottler.Throttle("AF_Mount", 3000)) return false;

        try
        {
            var am = ActionManager.Instance();
            if (c.PreferredMountId != 0)
            {
                // Verify the mount is unlocked before trying.
                if (PlayerState.Instance()->IsMountUnlocked(c.PreferredMountId))
                {
                    am->UseAction(ActionType.Mount, c.PreferredMountId);
                    return true;
                }
            }
            // Fallback: Mount Roulette general action.
            am->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction);
            return true;
        }
        catch (Exception e)
        {
            Svc.Log.Verbose($"[Mount] Mount failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Take off into flight. NOTE: vnavmesh already handles takeoff itself when you call its
    /// pathfind with fly=true, so this should ONLY be used as a manual fallback when we are mounted
    /// and stationary (not currently pathing). Calling it while vnavmesh is flying us causes the
    /// "endless jumping" bug, so the Navigator no longer calls this during active pathing.
    /// </summary>
    public static void EnsureAirborne(Configuration c)
    {
        if (!c.UseFlight) return;
        if (!IsMounted) return;
        if (IsFlying) return;
        if (Player.IsJumping) return; // already mid-jump; don't queue another
        if (!CanFlyHere) return;
        if (!EzThrottler.Throttle("AF_Takeoff", FlyJumpThrottleMs)) return;

        try
        {
            // Jump to take off (the game converts a jump into flight when airborne flight is available).
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2 /* Jump */);
        }
        catch (Exception e) { Svc.Log.Verbose($"[Mount] EnsureAirborne failed: {e.Message}"); }
    }

    public static bool IsFlying => Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight];

    /// <summary>Dismount via the Mount Roulette / mount general action toggle (id 9 toggles off when mounted).</summary>
    public static bool Dismount()
    {
        if (!IsMounted) return true;
        if (ECommons.GenericHelpers.IsOccupied()) return false;
        if (!EzThrottler.Throttle("AF_Dismount", 1500)) return false;
        try
        {
            // The "Mount" general action (id 9 = Mount Roulette) toggles dismount when already mounted.
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, MountRouletteGeneralAction);
            return false; // dismount animation; caller re-checks IsMounted next tick
        }
        catch (Exception e)
        {
            Svc.Log.Verbose($"[Mount] Dismount failed: {e.Message}");
            return false;
        }
    }

    /// <summary>Whether we should fly for this trip (flight unlocked + user enabled).</summary>
    public static bool ShouldFly(Configuration c) => c.UseFlight && CanFlyHere;

    // ------------------------------------------------- mount discovery for the UI
    public sealed record MountOption(uint Id, string Name);

    /// <summary>List the player's unlocked mounts so the UI can offer a preferred-mount picker.</summary>
    public static List<MountOption> ListUnlockedMounts()
    {
        var result = new List<MountOption> { new(0, "Mount Roulette") };
        try
        {
            var ps = PlayerState.Instance();
            var sheet = Svc.Data.GetExcelSheet<Mount>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0) continue;
                var name = row.Singular.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                if (!ps->IsMountUnlocked(row.RowId)) continue;
                result.Add(new MountOption(row.RowId, char.ToUpper(name[0]) + name[1..]));
            }
        }
        catch (Exception e) { Svc.Log.Verbose($"[Mount] ListUnlockedMounts failed: {e.Message}"); }
        return result.OrderBy(m => m.Id == 0 ? "" : m.Name).ToList();
    }
}
