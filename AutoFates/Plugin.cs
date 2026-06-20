using Dalamud.Game.Command;
using Dalamud.Plugin;
using ECommons;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.SimpleGui;

namespace AutoFates;

public sealed class Plugin : IDalamudPlugin
{
    public static Plugin? Instance { get; private set; }
    public static Configuration C { get; private set; } = null!;

    public Core.FarmingController Controller { get; }
    private readonly UI.MainWindow _window;

    public Plugin(IDalamudPluginInterface pi)
    {
        Instance = this;
        ECommonsMain.Init(pi, this, Module.DalamudReflector);
        C = EzConfig.Init<Configuration>();

        IPC.IPCManager.Init();
        Controller = new Core.FarmingController();

        _window = new UI.MainWindow();
        // Register our window directly with the EzConfig window system (handles UiBuilder wiring).
        EzConfigGui.Init(_window);

        EzCmd.Add("/autofates", OnCommand, "Open AutoFates. Use '/autofates start|stop|toggle' to control farming.");
        EzCmd.Add("/autofate", OnCommand, "Alias for /autofates.");
        EzCmd.Add("/af", OnCommand, "Alias for /autofates.");

        Svc.Framework.Update += OnUpdate;
        Svc.Log.Information("AutoFates loaded.");
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try { Controller.Tick(); }
        catch (Exception e)
        {
            Svc.Log.Error($"[AutoFates] Tick error: {e}");
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "start": Controller.Start(); break;
            case "stop": Controller.Stop(); break;
            case "toggle": Controller.Toggle(); break;
            case "":
            default:
                _window.IsOpen = !_window.IsOpen;
                break;
        }
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
        Controller.Stop();
        IPC.IPCManager.Dispose();
        EzConfig.Save();
        ECommonsMain.Dispose();
        Instance = null;
    }
}
