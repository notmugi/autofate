using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace AutoFates.Data;

/// <summary>
/// A farming zone entry. We key zones by their canonical English place name and resolve the
/// actual TerritoryType row id at runtime via Lumina, so we are not dependent on hardcoded ids
/// that can shift between game patches.
/// </summary>
public sealed class ZoneInfo
{
    public required string PlaceName { get; init; }
    /// <summary>Fallback TerritoryType id used if name resolution fails. 0 = resolve only by name.</summary>
    public uint FallbackTerritoryId { get; init; }
    /// <summary>Human friendly note (e.g. which atma/demiatma drops here).</summary>
    public string? Note { get; init; }

    private uint _resolvedTerritory;
    private bool _resolved;

    public uint TerritoryId
    {
        get
        {
            if (_resolved) return _resolvedTerritory;
            _resolvedTerritory = Zones.ResolveTerritoryByPlaceName(PlaceName, FallbackTerritoryId);
            _resolved = true;
            return _resolvedTerritory;
        }
    }
}

public static class Zones
{
    /// <summary>Resolve a TerritoryType row by matching its PlaceName (region/zone) string.</summary>
    public static uint ResolveTerritoryByPlaceName(string placeName, uint fallback = 0)
    {
        try
        {
            var sheet = Svc.Data.GetExcelSheet<TerritoryType>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0) continue;
                // Prefer overworld field zones (intended use 1) so we don't grab instanced duplicates.
                if (row.TerritoryIntendedUse.RowId != 1) continue;
                var pn = row.PlaceName.ValueNullable?.Name.ToString();
                if (string.IsNullOrEmpty(pn)) continue;
                if (string.Equals(pn, placeName, StringComparison.OrdinalIgnoreCase))
                    return row.RowId;
            }
            // Second pass: ignore intended-use restriction in case the zone uses a different code.
            foreach (var row in sheet)
            {
                if (row.RowId == 0) continue;
                var pn = row.PlaceName.ValueNullable?.Name.ToString();
                if (string.IsNullOrEmpty(pn)) continue;
                if (string.Equals(pn, placeName, StringComparison.OrdinalIgnoreCase))
                    return row.RowId;
            }
        }
        catch (Exception e)
        {
            Svc.Log.Warning($"[Zones] Failed to resolve '{placeName}': {e.Message}");
        }
        return fallback;
    }

    public static string GetTerritoryName(uint territoryId)
    {
        try
        {
            var row = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId);
            var pn = row?.PlaceName.ValueNullable?.Name.ToString();
            return string.IsNullOrEmpty(pn) ? $"#{territoryId}" : pn!;
        }
        catch { return $"#{territoryId}"; }
    }

    // ----- Atma (ARR Zodiac) zones: 12 -----
    // Fallback ids are the well-documented ARR overworld TerritoryType row ids.
    public static readonly ZoneInfo[] Atma =
    {
        new() { PlaceName = "Central Shroud",     FallbackTerritoryId = 148, Note = "Maiden's Atma" },
        new() { PlaceName = "Southern Thanalan",  FallbackTerritoryId = 146, Note = "Scorpion's Atma" },
        new() { PlaceName = "Upper La Noscea",    FallbackTerritoryId = 138, Note = "Watercarrier's Atma" },
        new() { PlaceName = "East Shroud",        FallbackTerritoryId = 152, Note = "Goat's Atma" },
        new() { PlaceName = "Eastern Thanalan",   FallbackTerritoryId = 145, Note = "Bull's Atma" },
        new() { PlaceName = "Middle La Noscea",   FallbackTerritoryId = 134, Note = "Ram's Atma" },
        new() { PlaceName = "Western Thanalan",   FallbackTerritoryId = 140, Note = "Twins' Atma" },
        new() { PlaceName = "Outer La Noscea",    FallbackTerritoryId = 180, Note = "Lion's Atma" },
        new() { PlaceName = "Lower La Noscea",    FallbackTerritoryId = 135, Note = "Fish's Atma" },
        new() { PlaceName = "North Shroud",       FallbackTerritoryId = 154, Note = "Archer's Atma" },
        new() { PlaceName = "Central Thanalan",   FallbackTerritoryId = 141, Note = "Scales' Atma" },
        new() { PlaceName = "Western La Noscea",  FallbackTerritoryId = 139, Note = "Crab's Atma" },
    };

    // ----- Demiatma (Dawntrail) zones: 6 -----
    public static readonly ZoneInfo[] Demiatma =
    {
        new() { PlaceName = "Urqopacha",        Note = "Azurite Demiatma" },
        new() { PlaceName = "Kozama'uka",       Note = "Verdigris Demiatma" },
        new() { PlaceName = "Yak T'el",         Note = "Malachite Demiatma" },
        new() { PlaceName = "Shaaloani",        Note = "Realgar Demiatma" },
        new() { PlaceName = "Heritage Found",   Note = "Caput Mortuum Demiatma" },
        new() { PlaceName = "Living Memory",    Note = "Orpiment Demiatma" },
    };

    // ----- Luminous Crystals (Heavensward zones): 6 -----
    public static readonly ZoneInfo[] LuminousCrystals =
    {
        new() { PlaceName = "Coerthas Western Highlands", Note = "Luminous Crystals" },
        new() { PlaceName = "The Sea of Clouds",          Note = "Luminous Crystals" },
        new() { PlaceName = "Azys Lla",                   Note = "Luminous Crystals" },
        new() { PlaceName = "The Dravanian Forelands",    Note = "Luminous Crystals" },
        new() { PlaceName = "The Churning Mists",         Note = "Luminous Crystals" },
        new() { PlaceName = "The Dravanian Hinterlands",  Note = "Luminous Crystals" },
    };

    // ----- Memories (Heavensward relic) zones: 3 -----
    public static readonly ZoneInfo[] Memories =
    {
        new() { PlaceName = "The Sea of Clouds",       Note = "Memories of the Dying" },
        new() { PlaceName = "The Dravanian Forelands", Note = "Memories of the Dying" },
        new() { PlaceName = "Azys Lla",                Note = "Memories of the Dying" },
    };

    public static ZoneInfo[] ForMode(FarmingMode mode) => mode switch
    {
        FarmingMode.Atma => Atma,
        FarmingMode.Demiatma => Demiatma,
        FarmingMode.LuminousCrystals => LuminousCrystals,
        FarmingMode.Memories => Memories,
        _ => Array.Empty<ZoneInfo>(),
    };

    /// <summary>
    /// Returns all field (overworld) TerritoryType rows that can host FATEs,
    /// as candidate zones the user can select for SingleZone / Manual modes.
    /// </summary>
    public static IEnumerable<(uint TerritoryId, string Name)> AllFieldZones()
    {
        var sheet = Svc.Data.GetExcelSheet<TerritoryType>();
        var seen = new HashSet<string>();
        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            // TerritoryIntendedUse 1 == open field zones that can host FATEs.
            if (row.TerritoryIntendedUse.RowId != 1) continue;
            var name = row.PlaceName.ValueNullable?.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            if (!seen.Add(name)) continue;
            yield return (row.RowId, name!);
        }
    }

    // Expansion (ExVersion) row ids: 0=ARR, 1=HW, 2=SB, 3=ShB, 4=EW, 5=DT.
    // Shared FATEs only exist in Shadowbringers, Endwalker, and Dawntrail field zones.
    public static readonly uint[] SharedFateExpansions = { 3, 4, 5 };

    /// <summary>
    /// Field zones that contain Shared FATEs (ShB / EW / DT overworld zones), built dynamically
    /// from the TerritoryType sheet so it stays correct across patches.
    /// </summary>
    public static IEnumerable<(uint TerritoryId, string Name)> SharedFateZones()
    {
        var sheet = Svc.Data.GetExcelSheet<TerritoryType>();
        var seen = new HashSet<string>();
        foreach (var row in sheet)
        {
            if (row.RowId == 0) continue;
            if (row.TerritoryIntendedUse.RowId != 1) continue;
            var ex = row.ExVersion.RowId;
            if (!SharedFateExpansions.Contains(ex)) continue;
            var name = row.PlaceName.ValueNullable?.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            if (!seen.Add(name)) continue;
            yield return (row.RowId, name!);
        }
    }
}
