using ECommons.DalamudServices;
using ECommons.Reflection;

namespace Autofate.IPC;

/// <summary>
/// Wrapper over TextAdvance's external-control IPC. This is how the reference plugins (BoT/DWD)
/// drive collect-fate turn-ins: instead of manually poking the Request addon (which the game
/// rejects — the context menu opens then instantly closes), they hand the ENTIRE turn-in flow to
/// TextAdvance via EnableExternalControl with RequestFill + RequestHandin (+ TalkSkip for the
/// surrounding dialogue). TextAdvance then fills the request slot and clicks Hand Over for us.
///
/// We take external control only while we're actively turning in, and release it the moment we're
/// done so we don't interfere with the user's normal dialogue elsewhere.
/// </summary>
public static class TextAdvanceIPC
{
    public const string InternalName = "TextAdvance";

    public static bool IsInstalled => DalamudReflector.TryGetDalamudPlugin(InternalName, out _, true, true);

    // TextAdvance's external-control config. Field names/types must match the type TextAdvance
    // exports over IPC (it deserializes by name). All nullable so unset fields keep TA's defaults.
    public sealed class ExternalTerritoryConfig
    {
        public bool? EnableQuestAccept;
        public bool? EnableQuestComplete;
        public bool? EnableRewardPick;
        public bool? EnableRequestHandin;
        public bool? EnableCutsceneEsc;
        public bool? EnableCutsceneSkipConfirm;
        public bool? EnableTalkSkip;
        public bool? EnableRequestFill;
        public bool? EnableAutoInteract;
    }

    private const string Owner = "Autofate";
    private static bool _controlActive;

    /// <summary>Whether we currently hold external control (local latch).</summary>
    public static bool ControlActive => _controlActive;

    public static bool IsInExternalControl()
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<bool>("TextAdvance.IsInExternalControl").InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>
    /// Take external control for the ENTIRE farming session so TextAdvance handles ALL of our
    /// dialogue: Talk advancing/skip, Yes/No confirmations, reward/quest pickers, cutscene skips,
    /// and the collect-fate Request window (fill + hand over). We drive movement and the actual
    /// NPC/object interaction ourselves (so we deliberately do NOT enable AutoInteract).
    ///
    /// SELF-HEALING: TextAdvance can silently DROP external control on its own (zone change, its own
    /// timeout, another consumer, plugin reload). We must NOT rely solely on our local latch — if we
    /// did, once it desynced we'd never re-assert and the Request window would sit unfilled forever
    /// (the "stuck at turn-in until I restart the plugin" bug). So we reconcile against TextAdvance's
    /// ACTUAL state every call and re-issue EnableExternalControl whenever it isn't in control.
    /// Safe to call every tick.
    /// </summary>
    public static void Enable()
    {
        if (!IsInstalled) return;

        // If TextAdvance already reports it's in external control, we're good — keep our latch in sync.
        if (IsInExternalControl())
        {
            _controlActive = true;
            return;
        }

        // Not in control (fresh start OR it dropped on us) -> (re)issue the request.
        var cfg = new ExternalTerritoryConfig
        {
            EnableTalkSkip = true,
            EnableRequestFill = true,
            EnableRequestHandin = true,
            EnableRewardPick = true,
            EnableCutsceneEsc = true,
            EnableCutsceneSkipConfirm = true,
            // NOT QuestAccept/QuestComplete: we never accept/complete quests while fate-farming, and
            // those flags make TextAdvance hand off to the Questionable plugin. We confirm fate-join
            // Yes/No prompts ourselves, so we don't need them.
            // NOT AutoInteract: we target + interact with NPCs/objects ourselves.
        };
        try
        {
            Svc.PluginInterface
               .GetIpcSubscriber<string, ExternalTerritoryConfig, bool>("TextAdvance.EnableExternalControl")
               .InvokeFunc(Owner, cfg);
            _controlActive = true;
        }
        catch (Exception e) { Svc.Log.Verbose($"[TextAdvance] EnableExternalControl failed: {e.Message}"); }
    }

    /// <summary>Release external control. Change-guarded so we only disable once.</summary>
    public static void Disable()
    {
        if (!_controlActive) return;
        _controlActive = false;
        if (!IsInstalled) return;
        try
        {
            Svc.PluginInterface
               .GetIpcSubscriber<string, bool>("TextAdvance.DisableExternalControl")
               .InvokeFunc(Owner);
        }
        catch (Exception e) { Svc.Log.Verbose($"[TextAdvance] DisableExternalControl failed: {e.Message}"); }
    }

    /// <summary>Force-reset the local control latch (used on hard Stop).</summary>
    public static void ResetCache() => _controlActive = false;
}
