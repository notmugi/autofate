using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Autofate.UI;

public sealed partial class MainWindow
{
    private List<Features.MountManager.MountOption>? _mounts;
    private string _mountSearch = string.Empty;

    private void DrawTravelTab()
    {
        ImGui.TextWrapped("How Autofate gets around. Long-distance travel uses vnavmesh; "
            + "between-zone travel uses Lifestream (or teleport).");
        ImGui.Separator();

        var useMount = C.UseMount;
        if (ImGui.Checkbox("Use a mount for long-distance travel", ref useMount)) { C.UseMount = useMount; Save(); }

        var useFlight = C.UseFlight;
        if (ImGui.Checkbox("Fly when flight is available", ref useFlight)) { C.UseFlight = useFlight; Save(); }

        var dist = C.MountDistanceThreshold;
        if (ImGui.SliderFloat("Mount if destination is farther than (yalms)", ref dist, 0, 100))
        {
            C.MountDistanceThreshold = dist; Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted($"Preferred mount: {C.PreferredMountName}");
        if (ImGui.Button("Refresh mount list")) _mounts = null;
        ImGui.SameLine();
        DrawMountPicker();

        ImGui.Separator();
        var useLs = C.UseLifestream;
        if (ImGui.Checkbox("Use Lifestream for teleporting between zones", ref useLs)) { C.UseLifestream = useLs; Save(); }


        ImGui.Spacing();
        var onFinish = C.LifestreamOnFinish;
        if (ImGui.Checkbox("Run a Lifestream command when farming finishes", ref onFinish)) { C.LifestreamOnFinish = onFinish; Save(); }
        if (C.LifestreamOnFinish)
        {
            var cmd = C.LifestreamFinishCommand;
            if (ImGui.InputText("Finish command", ref cmd, 128)) { C.LifestreamFinishCommand = cmd; Save(); }
            ImGui.SameLine(); Help("e.g. '/li home' or '/li <aetheryte>'. Runs after the plugin stops.");
        }
    }

    private void DrawMountPicker()
    {
        using var combo = ImRaii.Combo("##mountpicker", "Choose mount...");
        if (!combo) return;
        _mounts ??= Features.MountManager.ListUnlockedMounts();

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##mountsearch", "search", ref _mountSearch, 64);
        foreach (var m in _mounts)
        {
            if (!string.IsNullOrEmpty(_mountSearch)
                && !m.Name.Contains(_mountSearch, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ImGui.Selectable($"{m.Name}##{m.Id}"))
            {
                C.PreferredMountId = m.Id;
                C.PreferredMountName = m.Name;
                Save();
            }
        }
    }
}
