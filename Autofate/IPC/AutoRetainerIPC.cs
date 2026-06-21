using ECommons.DalamudServices;
using ECommons.Reflection;

namespace Autofate.IPC;

/// <summary>
/// Detection wrapper for AutoRetainer, used to detect it and avoid stepping on it while busy.
/// Actual item movement lives in <see cref="Autofate.Features.StorageManager"/> via game APIs.
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
