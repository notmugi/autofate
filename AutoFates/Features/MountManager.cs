using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace AutoFates.Features;

/// <summary>
/// Handles mounting and taking flight for long-distance travel. The user can pick a preferred
/// mount; if none (or "Mount Roulette") is set we use the Mount Roulette general action so we
/// don't need to know a specific unlocked mount.
/// </summary>
public static unsafe class MountManager
{
    private const uint MountRouletteGeneralAction = 9; // "Mount Roulette" general action
    private const int FlyJumpThrottleMs = 1500;

    public static bool IsMounted => Player.Mounted;

    public static bool CanMountHere => Player.CanMount;

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

    /// <summary>While mounted and flight is allowed, jump once to take off.</summary>
    public static void EnsureAirborne(Configuration c)
    {
        if (!c.UseFlight) return;
        if (!IsMounted) return;
        if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight]) return;
        if (!CanFlyHere) return;
        if (!EzThrottler.Throttle("AF_Takeoff", FlyJumpThrottleMs)) return;

        try
        {
            // Jump to take off (the game converts a jump into flight when airborne flight is available).
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2 /* Jump */);
        }
        catch (Exception e) { Svc.Log.Verbose($"[Mount] EnsureAirborne failed: {e.Message}"); }
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
