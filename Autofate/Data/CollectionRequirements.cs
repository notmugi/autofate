using ECommons.DalamudServices;
using Lumina.Excel.Sheets;

namespace Autofate.Data;

/// <summary>One collectable to farm: item (id resolved by name via Lumina), count needed, and drop zone(s).</summary>
public sealed class CollectItem
{
    public required string ItemName { get; init; }
    public required int Required { get; init; }
    /// <summary>Place names of the zones this item drops in (matched against ZoneInfo.PlaceName).</summary>
    public required string[] Zones { get; init; }

    private uint _resolvedId;
    private bool _resolved;

    /// <summary>Item row id, resolved from <see cref="ItemName"/> at runtime (0 if not found).</summary>
    public uint ItemId
    {
        get
        {
            if (_resolved) return _resolvedId;
            _resolvedId = CollectionRequirements.ResolveItemByName(ItemName);
            _resolved = true;
            return _resolvedId;
        }
    }
}

/// <summary>
/// Per-mode collectable requirements (Atma / Demiatma / Memories / Luminous Crystals). Moves on
/// when the current zone's items are satisfied; stops when the whole list is satisfied.
/// </summary>
public static class CollectionRequirements
{
    // Atma (ARR Zodiac): one of each, one zone per atma.
    public static readonly CollectItem[] Atma =
    {
        new() { ItemName = "Maiden's Atma",      Required = 1, Zones = new[] { "Central Shroud" } },
        new() { ItemName = "Scorpion's Atma",    Required = 1, Zones = new[] { "Southern Thanalan" } },
        new() { ItemName = "Watercarrier's Atma",Required = 1, Zones = new[] { "Upper La Noscea" } },
        new() { ItemName = "Goat's Atma",        Required = 1, Zones = new[] { "East Shroud" } },
        new() { ItemName = "Bull's Atma",        Required = 1, Zones = new[] { "Eastern Thanalan" } },
        new() { ItemName = "Ram's Atma",         Required = 1, Zones = new[] { "Middle La Noscea" } },
        new() { ItemName = "Twins' Atma",        Required = 1, Zones = new[] { "Western Thanalan" } },
        new() { ItemName = "Lion's Atma",        Required = 1, Zones = new[] { "Outer La Noscea" } },
        new() { ItemName = "Fish's Atma",        Required = 1, Zones = new[] { "Lower La Noscea" } },
        new() { ItemName = "Archer's Atma",      Required = 1, Zones = new[] { "North Shroud" } },
        new() { ItemName = "Scales' Atma",       Required = 1, Zones = new[] { "Central Thanalan" } },
        new() { ItemName = "Crab's Atma",        Required = 1, Zones = new[] { "Western La Noscea" } },
    };

    // Demiatma (Dawntrail): three of each, one zone per demiatma.
    public static readonly CollectItem[] Demiatma =
    {
        new() { ItemName = "Azurite Demiatma",      Required = 3, Zones = new[] { "Urqopacha" } },
        new() { ItemName = "Verdigris Demiatma",    Required = 3, Zones = new[] { "Kozama'uka" } },
        new() { ItemName = "Malachite Demiatma",    Required = 3, Zones = new[] { "Yak T'el" } },
        new() { ItemName = "Realgar Demiatma",      Required = 3, Zones = new[] { "Shaaloani" } },
        new() { ItemName = "Caput Mortuum Demiatma",Required = 3, Zones = new[] { "Heritage Found" } },
        new() { ItemName = "Orpiment Demiatma",     Required = 3, Zones = new[] { "Living Memory" } },
    };

    // Memories of the Dying (HW relic): 20 of each, two zones per memory.
    public static readonly CollectItem[] Memories =
    {
        new() { ItemName = "Tortured Memory of the Dying",  Required = 20,
                Zones = new[] { "Coerthas Western Highlands", "The Sea of Clouds" } },
        new() { ItemName = "Sorrowful Memory of the Dying", Required = 20,
                Zones = new[] { "The Dravanian Forelands", "The Churning Mists" } },
        new() { ItemName = "Harrowing Memory of the Dying", Required = 20,
                Zones = new[] { "The Dravanian Hinterlands", "Azys Lla" } },
    };

    // Luminous Crystals (HW relic): one of each, one zone per crystal.
    public static readonly CollectItem[] LuminousCrystals =
    {
        new() { ItemName = "Luminous Wind Crystal",      Required = 1, Zones = new[] { "The Sea of Clouds" } },
        new() { ItemName = "Luminous Fire Crystal",      Required = 1, Zones = new[] { "Azys Lla" } },
        new() { ItemName = "Luminous Lightning Crystal", Required = 1, Zones = new[] { "The Churning Mists" } },
        new() { ItemName = "Luminous Ice Crystal",       Required = 1, Zones = new[] { "Coerthas Western Highlands" } },
        new() { ItemName = "Luminous Earth Crystal",     Required = 1, Zones = new[] { "The Dravanian Forelands" } },
        new() { ItemName = "Luminous Water Crystal",     Required = 1, Zones = new[] { "The Dravanian Hinterlands" } },
    };

    /// <summary>Requirements for a given farming mode (empty for non-collection modes).</summary>
    public static CollectItem[] ForMode(FarmingMode mode) => mode switch
    {
        FarmingMode.Atma => Atma,
        FarmingMode.Demiatma => Demiatma,
        FarmingMode.Memories => Memories,
        FarmingMode.LuminousCrystals => LuminousCrystals,
        _ => System.Array.Empty<CollectItem>(),
    };

    /// <summary>True if this mode is a collectable-tracking mode.</summary>
    public static bool IsCollectionMode(FarmingMode mode) => ForMode(mode).Length > 0;

    /// <summary>True if every item in the mode's list is at or above its required count in inventory.</summary>
    public static bool AllSatisfied(FarmingMode mode)
    {
        foreach (var it in ForMode(mode))
            if (Features.InventoryUtil.GetItemCount(it.ItemId) < it.Required)
                return false;
        return true;
    }

    /// <summary>True if every collectable dropping in <paramref name="placeName"/> is satisfied (no reason to farm it).</summary>
    public static bool ZoneSatisfied(FarmingMode mode, string placeName)
    {
        foreach (var it in ForMode(mode))
        {
            if (System.Array.IndexOf(it.Zones, placeName) < 0) continue;
            if (Features.InventoryUtil.GetItemCount(it.ItemId) < it.Required)
                return false; // zone still has something we need
        }
        return true;
    }

    /// <summary>Zones (place names) in this mode that still have at least one unmet item.</summary>
    public static List<string> UnsatisfiedZones(FarmingMode mode)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var it in ForMode(mode))
        {
            if (Features.InventoryUtil.GetItemCount(it.ItemId) >= it.Required) continue;
            foreach (var z in it.Zones)
                if (seen.Add(z)) result.Add(z);
        }
        return result;
    }

    // Cache of item ids resolved by exact (case-insensitive) name.
    private static readonly Dictionary<string, uint> _idCache = new(System.StringComparer.OrdinalIgnoreCase);

    public static uint ResolveItemByName(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName)) return 0;
        if (_idCache.TryGetValue(itemName, out var cached)) return cached;
        uint found = 0;
        try
        {
            var sheet = Svc.Data.GetExcelSheet<Item>();
            foreach (var row in sheet)
            {
                if (row.RowId == 0) continue;
                if (string.Equals(row.Name.ToString(), itemName, System.StringComparison.OrdinalIgnoreCase))
                {
                    found = row.RowId;
                    break;
                }
            }
        }
        catch (System.Exception e) { Svc.Log.Verbose($"[Collection] ResolveItemByName('{itemName}') failed: {e.Message}"); }
        _idCache[itemName] = found;
        if (found == 0) Svc.Log.Warning($"[Collection] Could not resolve item id for '{itemName}'.");
        return found;
    }
}
