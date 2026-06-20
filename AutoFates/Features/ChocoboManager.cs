using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace AutoFates.Features;

/// <summary>
/// Manages the chocobo companion:
///  - Summoning / re-summoning via Gysahl Greens when the companion timer is about to expire.
///  - Setting and auto-switching stances (Defender / Attacker / Healer) including the
///    "drop to Healer when my HP is low" behaviour.
///  - Reading companion level (Rank), XP and summon time for the leveling logic / stop triggers.
///
/// The stable-leveling routine (go home, recall, stable, train, feed onions) is orchestrated by
/// <see cref="ChocoboStableRoutine"/> which is driven by the controller's TaskManager.
/// </summary>
public static unsafe class ChocoboManager
{
    // Resolved lazily from the GeneralAction sheet by name so we never hardcode a wrong id.
    private static uint _defenderId, _attackerId, _healerId, _gysahlId;
    private static bool _resolved;

    private static void ResolveActions()
    {
        if (_resolved) return;
        _resolved = true;
        try
        {
            var sheet = Svc.Data.GetExcelSheet<GeneralAction>();
            foreach (var row in sheet)
            {
                var name = row.Name.ToString();
                if (string.Equals(name, "Defender Stance", StringComparison.OrdinalIgnoreCase)) _defenderId = row.RowId;
                else if (string.Equals(name, "Attacker Stance", StringComparison.OrdinalIgnoreCase)) _attackerId = row.RowId;
                else if (string.Equals(name, "Healer Stance", StringComparison.OrdinalIgnoreCase)) _healerId = row.RowId;
                else if (string.Equals(name, "Gysahl Greens", StringComparison.OrdinalIgnoreCase)) _gysahlId = row.RowId;
            }
            Svc.Log.Debug($"[Chocobo] Resolved stances D={_defenderId} A={_attackerId} H={_healerId} Gysahl={_gysahlId}");
        }
        catch (Exception e) { Svc.Log.Warning($"[Chocobo] ResolveActions failed: {e.Message}"); }
    }

    // ----------------------------------------------------- companion state
    private static ref CompanionInfo Info => ref UIState.Instance()->Buddy.CompanionInfo;

    public static bool IsSummoned()
    {
        try { return UIState.Instance()->Buddy.CompanionInfo.TimeLeft > 0; }
        catch { return false; }
    }

    /// <summary>Companion summon time left, in seconds.</summary>
    public static float TimeLeft()
    {
        try { return UIState.Instance()->Buddy.CompanionInfo.TimeLeft; }
        catch { return 0; }
    }

    /// <summary>Chocobo rank (level), 1-20.</summary>
    public static int Rank()
    {
        try { return UIState.Instance()->Buddy.CompanionInfo.Rank; }
        catch { return 0; }
    }

    public static uint CurrentXP()
    {
        try { return UIState.Instance()->Buddy.CompanionInfo.CurrentXP; }
        catch { return 0; }
    }

    /// <summary>The currently active stance command (game value).</summary>
    public static byte ActiveCommand()
    {
        try { return UIState.Instance()->Buddy.CompanionInfo.ActiveCommand; }
        catch { return 0; }
    }

    // ----------------------------------------------------- summoning
    /// <summary>True if the player owns Gysahl Greens.</summary>
    public static bool HasGysahlGreens() => InventoryUtil.GetItemCount(Data.GameItems.GysahlGreens) > 0;

    /// <summary>Use a Gysahl Greens to summon / refresh the chocobo.</summary>
    public static bool UseGysahlGreens()
    {
        if (!HasGysahlGreens()) return false;
        if (!EzThrottler.Throttle("AF_Gysahl", 5_000)) return false;
        return InventoryUtil.UseItem(Data.GameItems.GysahlGreens);
    }

    /// <summary>
    /// Per-frame maintenance while farming with the companion enabled:
    ///  - re-summon when the timer is about to run out (and we have greens),
    ///  - keep the desired stance set,
    ///  - drop to Healer stance when HP is low, restore when recovered.
    /// </summary>
    public static void Tick(Configuration c)
    {
        if (!c.ChocoboCompanionEnabled) return;
        if (Player.Object == null || ECommons.GenericHelpers.IsOccupied()) return;
        ResolveActions();

        // Re-summon / refresh on low timer.
        if (c.AutoGysahlGreens)
        {
            var t = TimeLeft();
            if (t < c.GysahlReuseSeconds && HasGysahlGreens())
                UseGysahlGreens();
        }

        if (!IsSummoned()) return;

        // Stance management.
        ManageStance(c);
    }

    private static ChocoboStance _restoreStance = ChocoboStance.Attacker;

    private static void ManageStance(Configuration c)
    {
        var desired = c.ChocoboStance;

        if (c.AutoHealerStance)
        {
            var me = Player.Object;
            if (me != null)
            {
                var hpPct = me.MaxHp > 0 ? (int)(100f * me.CurrentHp / me.MaxHp) : 100;
                if (hpPct <= c.HealerStanceHpThreshold)
                {
                    desired = ChocoboStance.Healer;
                }
                else
                {
                    // recovered: restore the user's configured stance
                    _restoreStance = c.ChocoboStance;
                    desired = _restoreStance;
                }
            }
        }

        SetStance(desired);
    }

    /// <summary>Issue the stance change if not already in that stance (throttled).</summary>
    public static void SetStance(ChocoboStance stance)
    {
        if (stance == ChocoboStance.Follow) return;
        ResolveActions();

        var (actionId, command) = stance switch
        {
            ChocoboStance.Defender => (_defenderId, (byte)1),
            ChocoboStance.Attacker => (_attackerId, (byte)2),
            ChocoboStance.Healer => (_healerId, (byte)3),
            _ => (0u, (byte)0),
        };
        if (actionId == 0) return;
        if (ActiveCommand() == command) return; // already in stance
        if (!EzThrottler.Throttle($"AF_Stance_{actionId}", 3_000)) return;

        try
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, actionId);
            Svc.Log.Debug($"[Chocobo] Setting stance {stance}.");
        }
        catch (Exception e) { Svc.Log.Verbose($"[Chocobo] SetStance failed: {e.Message}"); }
    }

    /// <summary>True once the chocobo has hit the user's target level (or the hard cap of 20).</summary>
    public static bool ReachedTargetLevel(Configuration c)
        => Rank() >= Math.Min(20, Math.Max(1, c.ChocoboTargetLevel));

    public static bool HasCurielRoots() => InventoryUtil.GetItemCount(Data.GameItems.CurielRoot) > 0;
    public static bool HasThavnairianOnions() => InventoryUtil.GetItemCount(Data.GameItems.ThavnairianOnion) > 0;
    public static bool HasStableBrooms() => InventoryUtil.GetItemCount(Data.GameItems.MagickedStableBroom) > 0;
}
