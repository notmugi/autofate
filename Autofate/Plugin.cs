using Dalamud.Game.Command;
using Dalamud.Plugin;
using ECommons;
using ECommons.Configuration;
using ECommons.DalamudServices;
using ECommons.SimpleGui;

namespace Autofate;

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
        MigrateLegacyConfig(pi); // one-time copy of pre-rename "AutoFates" settings
        C = EzConfig.Init<Configuration>();
        // Collect fates are WIP/disabled — never keep them enabled in the mask (UI can't toggle it).
        C.EnabledFateTypes &= ~FateType.Collect;

        IPC.IPCManager.Init();
        Controller = new Core.FarmingController();

        _window = new UI.MainWindow();
        // Register our window directly with the EzConfig window system (handles UiBuilder wiring).
        EzConfigGui.Init(_window);
        // Also expose the window as the plugin's main UI entrypoint (the title-screen/installer
        // "open" button), satisfying Dalamud's OpenMainUi convention.
        Svc.PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;

        EzCmd.Add("/autofates", OnCommand, "Open Autofate. Use '/autofates start|stop|toggle' to control farming.");
        EzCmd.Add("/autofate", OnCommand, "Alias for /autofates.");
        EzCmd.Add("/af", OnCommand, "Alias for /autofates.");

        Svc.Framework.Update += OnUpdate;
        Svc.Log.Information("Autofate loaded.");
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try { Controller.Tick(); }
        catch (Exception e)
        {
            Svc.Log.Error($"[Autofate] Tick error: {e}");
        }
    }

    /// <summary>Reset ALL config back to defaults (including user-specific setup).</summary>
    public static void ResetToDefaults()
    {
        C = EzConfig.Set(new Configuration());
        EzConfig.Save();
        Svc.Log.Information("[Autofate] Config reset to defaults.");
    }

    // The plugin was renamed AutoFates -> Autofate. Dalamud keys config by InternalName, so old
    // settings live in the sibling "AutoFates" config dir. Copy them once into our new dir if it's
    // empty, so existing users keep their setup.
    private static void MigrateLegacyConfig(IDalamudPluginInterface pi)
    {
        try
        {
            var newDir = pi.GetPluginConfigDirectory();           // .../pluginConfigs/Autofate
            var oldDir = Path.Combine(Directory.GetParent(newDir)!.FullName, "AutoFates");
            if (!Directory.Exists(oldDir)) return;
            Directory.CreateDirectory(newDir);
            if (Directory.EnumerateFileSystemEntries(newDir).Any()) return; // already have config

            foreach (var file in Directory.GetFiles(oldDir))
                File.Copy(file, Path.Combine(newDir, Path.GetFileName(file)), overwrite: false);

            // Also migrate the single-file config (pluginConfigs/AutoFates.json) if present.
            var oldFile = Path.Combine(Directory.GetParent(newDir)!.FullName, "AutoFates.json");
            var newFile = Path.Combine(Directory.GetParent(newDir)!.FullName, "Autofate.json");
            if (File.Exists(oldFile) && !File.Exists(newFile))
                File.Copy(oldFile, newFile, overwrite: false);

            Svc.Log.Information("[Autofate] Migrated legacy AutoFates config.");
        }
        catch (Exception e) { Svc.Log.Warning($"[Autofate] Config migration skipped: {e.Message}"); }
    }

    private void OpenMainUi() => _window.IsOpen = true;

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
        Svc.PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        Controller.Stop();
        IPC.IPCManager.Dispose();
        EzConfig.Save();
        ECommonsMain.Dispose();
        Instance = null;
    }
}
