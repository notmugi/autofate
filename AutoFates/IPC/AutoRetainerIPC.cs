using ECommons.DalamudServices;
using ECommons.Reflection;

namespace AutoFates.IPC;

/// <summary>
/// Lightweight detection wrapper for AutoRetainer. AutoRetainer's public API is centered on
/// venture post-processing rather than ad-hoc item withdrawal, so the actual saddlebag / retainer
/// item movement is performed in <see cref="AutoFates.Features.StorageManager"/> via game APIs.
/// This wrapper is used to detect AutoRetainer and to avoid stepping on it while it is busy.
/// </summary>
public static class AutoRetainerIPC
{
    public const string InternalName = "AutoRetainer";

    public static bool IsInstalled => DalamudReflector.TryGetDalamudPlugin(InternalName, out _, true, true);

    public static bool IsBusy()
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<bool>("AutoRetainer.IsBusy").InvokeFunc(); }
        catch { return false; }
    }

    public static void AbortAllTasks()
    {
        try { Svc.PluginInterface.GetIpcSubscriber<object>("AutoRetainer.AbortAllTasks").InvokeAction(); }
        catch (Exception e) { Svc.Log.Verbose($"[AutoRetainer] AbortAllTasks failed: {e.Message}"); }
    }
}
