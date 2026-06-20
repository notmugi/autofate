using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;

namespace AutoFates.UI;

public sealed partial class MainWindow
{
    // Cached zone list for the picker (built lazily; cheap to rebuild on demand).
    private List<(uint Id, string Name)>? _fieldZones;
    private string _zoneSearch = string.Empty;

    private List<(uint Id, string Name)> FieldZones()
        => _fieldZones ??= Data.Zones.AllFieldZones().OrderBy(z => z.Name).ToList();

    private void DrawModeTab()
    {
        ImGui.TextWrapped("Choose what kind of FATE farming to do.");
        ImGui.Spacing();

        var mode = C.Mode;
        if (ImGuiEx.EnumCombo("Farming Mode", ref mode, ModeNames))
        {
            C.Mode = mode;
            Save();
        }

        ImGui.Separator();

        switch (C.Mode)
        {
            case FarmingMode.Leveling: DrawLevelingMode(); break;
            case FarmingMode.SingleZone: DrawSingleZoneMode(); break;
            case FarmingMode.SharedFates: DrawSharedFatesMode(); break;
            case FarmingMode.Atma:
            case FarmingMode.Demiatma:
            case FarmingMode.LuminousCrystals:
            case FarmingMode.Memories:
                DrawFixedZoneMode();
                break;
            case FarmingMode.Manual: DrawManualMode(); break;
        }
    }

    private static readonly Dictionary<FarmingMode, string> ModeNames = new()
    {
        [FarmingMode.Leveling] = "Leveling (current class)",
        [FarmingMode.SingleZone] = "Single Zone",
        [FarmingMode.SharedFates] = "Shared FATEs (ShB/EW/DT)",
        [FarmingMode.Atma] = "Atma (ARR Zodiac)",
        [FarmingMode.Demiatma] = "Demiatma (Dawntrail)",
        [FarmingMode.LuminousCrystals] = "Luminous Crystals (HW)",
        [FarmingMode.Memories] = "Memories (HW relic)",
        [FarmingMode.Manual] = "Manual selection",
    };

    private void DrawLevelingMode()
    {
        ImGui.TextWrapped("Farms FATEs to level your current class. The plugin will keep running "
            + "fates in valid zones until you reach the desired level.");
        ImGui.Spacing();
        var lvl = C.DesiredLevel;
        if (ImGui.InputInt("Desired level", ref lvl))
        {
            C.DesiredLevel = Math.Clamp(lvl, 1, 100);
            C.StopAtLevel = true;
            Save();
        }
        ImGui.TextDisabled($"Current level: {ECommons.GameHelpers.Player.Level}");
    }

    private void DrawSingleZoneMode()
    {
        ImGui.TextWrapped("Farms FATEs in a single zone.");
        ImGui.Spacing();

        var current = Svc.ClientState.TerritoryType;
        if (ImGui.Button("Set to current zone"))
        {
            C.SingleZoneTerritory = current;
            Save();
        }
        ImGui.SameLine();
        var name = C.SingleZoneTerritory != 0 ? Data.Zones.GetTerritoryName(C.SingleZoneTerritory) : "<none>";
        ImGui.TextUnformatted($"Selected: {name}");

        ImGui.Spacing();
        DrawZonePicker("##singlezonepicker", id =>
        {
            C.SingleZoneTerritory = id;
            Save();
        });
    }

    private void DrawSharedFatesMode()
    {
        ImGui.TextWrapped("Shared FATEs are simply the FATEs in Shadowbringers, Endwalker, and "
            + "Dawntrail overworld zones. The plugin rotates through these zones automatically.");
        ImGui.Spacing();
        var zones = Data.Zones.SharedFateZones().OrderBy(z => z.Name).ToList();
        ImGui.TextDisabled($"{zones.Count} shared-fate zones detected.");
        using var box = ImRaii.Child("##sfzones", new System.Numerics.Vector2(0, 200), true);
        foreach (var z in zones)
            ImGui.TextUnformatted(z.Name);
    }

    private void DrawFixedZoneMode()
    {
        var zones = Data.Zones.ForMode(C.Mode);
        ImGui.TextWrapped($"This mode rotates through {zones.Length} fixed zones:");
        ImGui.Spacing();
        using var tbl = ImRaii.Table("##fixedzones", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg);
        if (tbl)
        {
            ImGui.TableSetupColumn("Zone");
            ImGui.TableSetupColumn("Drops");
            ImGui.TableHeadersRow();
            foreach (var z in zones)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(z.PlaceName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(z.Note ?? "");
            }
        }
    }

    private void DrawManualMode()
    {
        ImGui.TextWrapped("Build a custom list of zones, set how many fates to run in each, and "
            + "optionally loop the list.");
        ImGui.Spacing();

        var loop = C.ManualLoop;
        if (ImGui.Checkbox("Loop list", ref loop)) { C.ManualLoop = loop; Save(); }

        ImGui.Separator();

        // Existing entries.
        ManualZoneEntry? toRemove = null;
        using (var tbl = ImRaii.Table("##manualzones", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            if (tbl)
            {
                ImGui.TableSetupColumn("Zone", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Fates", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("Done", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("##act", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableHeadersRow();

                foreach (var entry in C.ManualZones)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(entry.Name);

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    var n = entry.FatesToRun;
                    if (ImGui.InputInt($"##fates{entry.GetHashCode()}", ref n))
                    {
                        entry.FatesToRun = Math.Max(0, n);
                        Save();
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{entry.FatesDone}");
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Reset##{entry.GetHashCode()}"))
                    {
                        entry.ResetCounter();
                        Save();
                    }

                    ImGui.TableNextColumn();
                    if (ImGui.SmallButton($"Remove##{entry.GetHashCode()}"))
                        toRemove = entry;
                }
            }
        }
        if (toRemove != null)
        {
            C.ManualZones.Remove(toRemove);
            Save();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Add a zone:");
        if (ImGui.Button("Add current zone"))
        {
            AddManualZone(Svc.ClientState.TerritoryType);
        }
        DrawZonePicker("##manualpicker", AddManualZone);
    }

    private void AddManualZone(uint territoryId)
    {
        if (territoryId == 0) return;
        if (C.ManualZones.Any(z => z.TerritoryId == territoryId)) return;
        C.ManualZones.Add(new ManualZoneEntry
        {
            TerritoryId = territoryId,
            Name = Data.Zones.GetTerritoryName(territoryId),
            FatesToRun = 5,
        });
        Save();
    }

    /// <summary>A searchable zone dropdown; invokes onPick with the selected territory id.</summary>
    private void DrawZonePicker(string id, Action<uint> onPick)
    {
        using var combo = ImRaii.Combo(id, "Select a zone...");
        if (!combo) return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##zonesearch", "search", ref _zoneSearch, 64);
        foreach (var z in FieldZones())
        {
            if (!string.IsNullOrEmpty(_zoneSearch)
                && !z.Name.Contains(_zoneSearch, StringComparison.OrdinalIgnoreCase))
                continue;
            if (ImGui.Selectable($"{z.Name}##{z.Id}"))
                onPick(z.Id);
        }
    }
}
