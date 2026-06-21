using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Autofate.Features;

/// <summary>
/// Manages the chocobo companion: summoning, stance auto-switching, and reading rank/XP/timer.
/// The stable-leveling routine is orchestrated by <see cref="ChocoboStableRoutine"/>.
/// </summary>
public static unsafe class ChocoboManager
{
    // Companion commands are BuddyAction RowIds (NOT GeneralAction), issued via ActionType.BuddyAction:
    //   2=Withdraw, 3=Follow, 4=Free Stance, 5=Defender, 6=Attacker, 7=Healer.
    // CompanionInfo.ActiveCommand holds the active stance's BuddyAction RowId directly.
    private const uint BuddyWithdraw = 2;
    private const uint BuddyDefender = 5;
    private const uint BuddyAttacker = 6;
    private const uint BuddyHealer   = 7;

    /// <summary>
    /// Dismiss the companion via Withdraw (BuddyAction #2). Required before stabling. Returns true if issued.
    /// </summary>
    public static bool Recall()
    {
        if (!IsSummoned()) return true; // already dismissed
        if (!EzThrottler.Throttle("AF_Withdraw", 3_000)) return false;
        try
        {
            ActionManager.Instance()->UseAction(ActionType.BuddyAction, BuddyWithdraw);
            Svc.Log.Debug("[Chocobo] Withdraw issued (dismissing companion).");
            return true;
        }
        catch (Exception e) { Svc.Log.Verbose($"[Chocobo] Withdraw failed: {e.Message}"); return false; }
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

    /// <summary>
    /// XP needed to fill the chocobo's current rank. CompanionInfo only exposes CurrentXP; the cap
    /// lives in the BuddyRank Excel sheet (keyed by rank, ExpRequired column).
    /// </summary>
    public static uint MaxXP()
    {
        try
        {
            var rank = (uint)Rank();
            if (rank == 0) return 0;
            var sheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.BuddyRank>();
            var row = sheet?.GetRowOrDefault(rank);
            return row?.ExpRequired ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// True when the chocobo's XP is capped for its current rank and it's ready to be stabled/trained.
    /// We only stable once capped, to avoid interrupting farming while it still needs FATE XP.
    /// </summary>
    public static bool XpMaxed()
    {
        var max = MaxXP();
        if (max == 0) return false; // unknown / not summoned — don't trigger stabling
        return CurrentXP() >= max;
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
    /// Per-frame maintenance: re-summon on low timer, keep desired stance, and auto-Healer when HP is low.
    /// </summary>
    public static void Tick(Configuration c)
    {
        if (!c.ChocoboCompanionEnabled) return;
        if (Player.Object == null || ECommons.GenericHelpers.IsOccupied()) return;

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

    /// <summary>
    /// Companion HP percent (0-100), via Svc.Buddies.CompanionBuddy. Falls back to 100 when unreadable.
    /// </summary>
    public static int ChocoboHpPercent()
    {
        try
        {
            var comp = Svc.Buddies.CompanionBuddy;
            var go = comp?.GameObject;
            if (go is Dalamud.Game.ClientState.Objects.Types.IBattleChara bc && bc.MaxHp > 0)
                return (int)(100f * bc.CurrentHp / bc.MaxHp);
        }
        catch { }
        return 100;
    }

    private static void ManageStance(Configuration c)
    {
        var desired = c.ChocoboStance;

        if (c.AutoHealerStance)
        {
            var me = Player.Object;
            if (me != null)
            {
                var myPct = me.MaxHp > 0 ? (int)(100f * me.CurrentHp / me.MaxHp) : 100;
                // Switch to Healer if either the player or the chocobo drops below the threshold.
                var chocoPct = ChocoboHpPercent();
                var lowest = System.Math.Min(myPct, chocoPct);
                if (lowest <= c.HealerStanceHpThreshold)
                {
                    desired = ChocoboStance.Healer;
                }
                else
                {
                    // Recovered: restore the configured stance.
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

        // Stance maps directly to a BuddyAction RowId, same id stored in ActiveCommand.
        var actionId = stance switch
        {
            ChocoboStance.Defender => BuddyDefender,
            ChocoboStance.Attacker => BuddyAttacker,
            ChocoboStance.Healer   => BuddyHealer,
            _ => 0u,
        };
        if (actionId == 0) return;
        if (ActiveCommand() == actionId) return; // already in stance
        if (!EzThrottler.Throttle($"AF_Stance_{actionId}", 3_000)) return;

        try
        {
            ActionManager.Instance()->UseAction(ActionType.BuddyAction, actionId);
            Svc.Log.Debug($"[Chocobo] Setting stance {stance} (BuddyAction {actionId}).");
        }
        catch (Exception e) { Svc.Log.Verbose($"[Chocobo] SetStance failed: {e.Message}"); }
    }

    /// <summary>True once the chocobo has hit the user's target level (or the hard cap of 20).</summary>
    public static bool ReachedTargetLevel(Configuration c)
        => Rank() >= Math.Min(20, Math.Max(1, c.ChocoboTargetLevel));

    public static bool HasThavnairianOnions() => InventoryUtil.GetItemCount(Data.GameItems.ThavnairianOnion) > 0;
    public static bool HasStableBrooms() => InventoryUtil.GetItemCount(Data.GameItems.MagickedStableBroom) > 0;
}
