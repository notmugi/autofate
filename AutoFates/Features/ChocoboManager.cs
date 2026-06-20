using System.Linq;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

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
    // Chocobo companion commands are BuddyAction rows, issued via ActionType.BuddyAction
    // (NOT GeneralAction — that sheet has no companion commands, which is why name lookups there
    // always returned 0 and spammed "not resolved"). These RowIds are authoritative and stable:
    //   BuddyAction:  2=Withdraw, 3=Follow, 4=Free Stance, 5=Defender, 6=Attacker, 7=Healer.
    // CompanionInfo.ActiveCommand holds the active stance's BuddyAction RowId directly.
    private const uint BuddyWithdraw = 2;
    private const uint BuddyDefender = 5;
    private const uint BuddyAttacker = 6;
    private const uint BuddyHealer   = 7;

    /// <summary>
    /// Dismiss the chocobo companion by executing the "Withdraw" buddy action (BuddyAction #2).
    /// Required before stabling (you can't stable a summoned chocobo). Returns true if issued.
    /// NOT /companion (that just toggles the companion menu open).
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

        // Stance = BuddyAction RowId. ActiveCommand holds the active stance's BuddyAction RowId
        // directly, so we compare against the same id (no separate command mapping needed).
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

    public static bool HasCurielRoots() => InventoryUtil.GetItemCount(Data.GameItems.CurielRoot) > 0;
    public static bool HasThavnairianOnions() => InventoryUtil.GetItemCount(Data.GameItems.ThavnairianOnion) > 0;
    public static bool HasStableBrooms() => InventoryUtil.GetItemCount(Data.GameItems.MagickedStableBroom) > 0;
}
