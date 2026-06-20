using ECommons.Automation;
using ECommons.DalamudServices;
using ECommons.Reflection;

namespace AutoFates.IPC;

/// <summary>Wrapper over Lifestream. Lifestream exposes most travel as text commands plus a few IPC funcs.</summary>
public static class LifestreamIPC
{
    public const string Name = "Lifestream";

    public static bool IsInstalled => DalamudReflector.TryGetDalamudPlugin(Name, out _, true, true);

    public static bool IsBusy()
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(); }
        catch { return false; }
    }

    /// <summary>Run an arbitrary lifestream command, e.g. "/li home", "/li &lt;aetheryte&gt;".</summary>
    public static void ExecuteCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (!command.StartsWith('/')) command = "/li " + command;
        Chat.ExecuteCommand(command);
    }

    /// <summary>Teleport to an aetheryte by name via the standard lifestream command.</summary>
    public static void TeleportToAetheryte(string aetheryteName)
        => ExecuteCommand($"/li {aetheryteName}");

    public static void AethernetTeleportById(uint aethernetId)
    {
        try { Svc.PluginInterface.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById").InvokeFunc(aethernetId); }
        catch (Exception e) { Svc.Log.Verbose($"[Lifestream] AethernetTeleportById failed: {e.Message}"); }
    }
}
