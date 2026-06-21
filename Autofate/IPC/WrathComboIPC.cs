using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.Reflection;

namespace Autofate.IPC;

#pragma warning disable CS0649, CS8618, CS0169

/// <summary>
/// WrathCombo IPC wrapper (official IPCExample.cs pattern): register a lease with a cancellation
/// callback and drive auto-rotation.
/// </summary>
public static class WrathComboIPC
{
    public const string InternalName = "WrathCombo";
    private const string CallbackPrefix = "AutofateWrathCallback";

    private static EzIPCDisposalToken[] _disposal = Array.Empty<EzIPCDisposalToken>();
    private static bool _initialized;
    private static Guid? _lease;

    public static bool IsInstalled => DalamudReflector.TryGetDalamudPlugin(InternalName, out _, true, true);

    public enum SetResult
    {
        Okay = 0,
        OkayWorking = 1,
        IPCDisabled = 10,
        InvalidLease = 11,
        BlacklistedLease = 12,
        Duplicate = 13,
        PlayerNotAvailable = 14,
        InvalidConfiguration = 15,
        InvalidValue = 16,
    }

    public enum AutoRotationConfigOption
    {
        InCombatOnly = 0,
        DPSRotationMode = 1,
        HealerRotationMode = 2,
        FATEPriority = 3,
        QuestPriority = 4,
        SingleTargetHPP = 5,
        AoETargetHPP = 6,
        SingleTargetRegenHPP = 7,
        ManageKardia = 8,
        AutoRez = 9,
        AutoRezDPSJobs = 10,
        AutoCleanse = 11,
        IncludeNPCs = 12,
        OnlyAttackInCombat = 13,
    }

    [EzIPC] internal static Func<string, string, string?, Guid?> RegisterForLeaseWithCallback;
    [EzIPC] internal static Func<Guid, bool, SetResult> SetAutoRotationState;
    [EzIPC] internal static Func<Guid, SetResult> SetCurrentJobAutoRotationReady;
    [EzIPC] internal static Func<Guid, AutoRotationConfigOption, object, SetResult> SetAutoRotationConfigState;
    [EzIPC] internal static Action<Guid> ReleaseControl;

    public static void Init()
    {
        if (_initialized) return;
        try
        {
            _disposal = EzIPC.Init(typeof(WrathComboIPC), InternalName, SafeWrapper.IPCException);
            _initialized = true;
        }
        catch (Exception e)
        {
            Svc.Log.Warning($"[WrathCombo] EzIPC init failed (plugin probably not installed): {e.Message}");
        }
    }

    private static Guid? EnsureLease()
    {
        if (_lease != null) return _lease;
        if (!_initialized || RegisterForLeaseWithCallback == null) return null;
        try
        {
            _lease = RegisterForLeaseWithCallback(InternalName, "Autofate", CallbackPrefix);
            if (_lease == null)
                Svc.Log.Warning("[WrathCombo] Failed to register lease (see Wrath logs).");
        }
        catch (Exception e)
        {
            Svc.Log.Warning($"[WrathCombo] Lease registration error: {e.Message}");
        }
        return _lease;
    }

    /// <summary>Enable auto-rotation, make the current job ready, and prioritize FATE targets.</summary>
    public static bool Enable()
    {
        if (!IsInstalled) return false;
        Init();
        var lease = EnsureLease();
        if (lease == null) return false;
        try
        {
            SetAutoRotationState(lease.Value, true);
            SetCurrentJobAutoRotationReady(lease.Value);
            // CRITICAL: both default to true in Wrath, making it wait for a mob to aggro us first.
            // Force OFF so Wrath actively opens combat on the fate mob we target.
            SetAutoRotationConfigState(lease.Value, AutoRotationConfigOption.InCombatOnly, false);
            SetAutoRotationConfigState(lease.Value, AutoRotationConfigOption.OnlyAttackInCombat, false);
            SetAutoRotationConfigState(lease.Value, AutoRotationConfigOption.FATEPriority, true);
            return true;
        }
        catch (Exception e)
        {
            Svc.Log.Warning($"[WrathCombo] Enable failed: {e.Message}");
            return false;
        }
    }

    public static void Disable()
    {
        if (_lease == null) return;
        try
        {
            SetAutoRotationState?.Invoke(_lease.Value, false);
            ReleaseControl?.Invoke(_lease.Value);
        }
        catch (Exception e) { Svc.Log.Verbose($"[WrathCombo] Disable error: {e.Message}"); }
        _lease = null;
    }

    public static void Dispose()
    {
        Disable();
        foreach (var t in _disposal)
        {
            try { t.Dispose(); } catch { /* ignore */ }
        }
        _disposal = Array.Empty<EzIPCDisposalToken>();
        _initialized = false;
    }
}

/// <summary>Receives WrathCombo's lease cancellation callback.</summary>
public class WrathComboCallbackReceiver
{
    public WrathComboCallbackReceiver()
    {
        try { EzIPC.Init(this, prefix: "AutofateWrathCallback"); }
        catch (Exception e) { Svc.Log.Verbose($"[WrathCombo] callback init failed: {e.Message}"); }
    }

    [EzIPC]
    public void WrathComboCallback(int reason, string additionalInfo)
    {
        Svc.Log.Warning($"[WrathCombo] Lease cancelled (reason {reason}): {additionalInfo}");
    }
}
