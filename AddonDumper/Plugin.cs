using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AddonDumper;

public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;

    private bool _open = true;
    private string _filter = string.Empty;
    private bool _includeText = true;
    private readonly List<AddonInfo> _snapshot = new();

    private record struct AddonInfo(string Name, uint Id, bool Visible, Vector2 Pos, List<string> Texts);

    public Plugin()
    {
        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += () => _open = true;
        CommandManager.AddHandler("/addondump", new CommandInfo((_, _) => { _open = true; })
        {
            HelpMessage = "Open the AddonDumper window."
        });
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        CommandManager.RemoveHandler("/addondump");
    }

    private void Dump()
    {
        _snapshot.Clear();
        try
        {
            var mgr = RaptureAtkUnitManager.Instance();
            ref var list = ref mgr->AtkUnitManager.AllLoadedUnitsList;
            for (var i = 0; i < list.Count; i++)
            {
                var u = list.Entries[i].Value;
                if (u == null) continue;
                var name = u->NameString;
                if (string.IsNullOrEmpty(name)) continue;

                var texts = new List<string>();
                if (_includeText)
                    CollectText(u, texts);

                _snapshot.Add(new AddonInfo(name, u->Id, u->IsVisible,
                    new Vector2(u->X, u->Y), texts));
            }
            _snapshot.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

            // Also echo to the log for easy copy/paste.
            var visibleNames = _snapshot.Where(a => a.Visible).Select(a => a.Name);
            Log.Information($"[AddonDumper] Visible addons: {string.Join(", ", visibleNames)}");
        }
        catch (Exception e)
        {
            Log.Error($"[AddonDumper] Dump failed: {e.Message}");
        }
    }

    private static void CollectText(AtkUnitBase* u, List<string> outList)
    {
        try
        {
            var n = u->UldManager.NodeListCount;
            for (var i = 0; i < n; i++)
            {
                var node = u->UldManager.NodeList[i];
                if (node == null) continue;
                if (node->Type != NodeType.Text) continue;
                var t = (AtkTextNode*)node;
                var s = t->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    outList.Add(s.Trim());
            }
        }
        catch { /* ignore per-addon text errors */ }
    }

    private void Draw()
    {
        if (!_open) return;
        ImGui.SetNextWindowSize(new Vector2(520, 600), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("AddonDumper", ref _open))
        {
            ImGui.End();
            return;
        }

        if (ImGui.Button("DUMP visible addons"))
            Dump();
        ImGui.SameLine();
        ImGui.Checkbox("include text nodes", ref _includeText);
        ImGui.SameLine();
        ImGui.TextDisabled($"({_snapshot.Count} found)");

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "filter by name...", ref _filter, 128);

        ImGui.Separator();
        if (ImGui.BeginChild("list"))
        {
            foreach (var a in _snapshot)
            {
                if (!string.IsNullOrEmpty(_filter)
                    && !a.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var header = $"{(a.Visible ? "[V]" : "[ ]")} {a.Name}  (id {a.Id})";
                if (ImGui.TreeNode(header))
                {
                    ImGui.TextDisabled($"pos ({a.Pos.X:0},{a.Pos.Y:0}), {a.Texts.Count} text nodes");
                    if (ImGui.SmallButton($"copy name##{a.Id}"))
                        ImGui.SetClipboardText(a.Name);
                    foreach (var t in a.Texts)
                        ImGui.BulletText(t);
                    ImGui.TreePop();
                }
            }
        }
        ImGui.EndChild();
        ImGui.End();
    }
}
