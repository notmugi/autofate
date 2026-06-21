namespace Autofate.Data;

/// <summary>
/// Best FATE-grinding zone per player level. Picks the highest entry whose MinLevel is &lt;= the
/// player level, then resolves its TerritoryType by place name.
/// </summary>
public static class LevelingZones
{
    public sealed record LevelZone(int MinLevel, string PlaceName);

    // Ascending by MinLevel; picker selects the last entry with MinLevel <= playerLevel.
    public static readonly LevelZone[] All =
    {
        new(1,  "Central Shroud"),
        new(5,  "Central Shroud"),
        new(8,  "Western Thanalan"),
        new(10, "Eastern Thanalan"),
        new(15, "East Shroud"),
        new(20, "South Shroud"),
        new(25, "Eastern La Noscea"),
        new(30, "Southern Thanalan"),
        new(35, "Coerthas Central Highlands"),
        new(40, "Western La Noscea"),
        new(44, "Mor Dhona"),
        new(45, "Coerthas Central Highlands"),
        new(49, "Northern Thanalan"),
        // Heavensward
        new(50, "The Sea of Clouds"),
        new(51, "Coerthas Western Highlands"),
        new(52, "The Dravanian Forelands"),
        new(54, "The Churning Mists"),
        new(56, "Coerthas Western Highlands"),
        new(57, "The Sea of Clouds"),
        new(58, "The Dravanian Hinterlands"),
        new(59, "Azys Lla"),
        // Stormblood
        new(60, "The Fringes"),
        new(62, "The Ruby Sea"),
        new(64, "Yanxia"),
        new(65, "The Azim Steppe"),
        new(67, "Yanxia"),
        new(68, "The Peaks"),
        new(69, "The Lochs"),
        // Shadowbringers
        new(70, "Kholusia"),
        new(71, "Lakeland"),
        new(72, "Il Mheg"),
        new(74, "The Rak'tika Greatwood"),
        new(76, "Amh Araeng"),
        new(78, "Kholusia"),
        new(79, "The Tempest"),
        // Endwalker
        new(80, "Thavnair"),
        new(82, "Garlemald"),
        new(83, "Mare Lamentorum"),
        new(85, "Labyrinthos"),
        new(86, "Elpis"),
        new(88, "Labyrinthos"),
        new(89, "Ultima Thule"),
        // Dawntrail
        new(90, "Kozama'uka"),
        new(93, "Urqopacha"),
        new(94, "Yak T'el"),
        new(95, "Shaaloani"),
        new(97, "Heritage Found"),
        new(99, "Living Memory"),
    };

    /// <summary>Best leveling zone's TerritoryId for the given player level (0 if unresolved).</summary>
    public static uint BestTerritoryForLevel(int level)
    {
        var pick = All[0];
        foreach (var z in All)
        {
            if (z.MinLevel <= level) pick = z;
            else break;
        }
        return Zones.ResolveTerritoryByPlaceName(pick.PlaceName);
    }

    /// <summary>Place name of the best leveling zone for the given level.</summary>
    public static string BestZoneNameForLevel(int level)
    {
        var pick = All[0];
        foreach (var z in All)
        {
            if (z.MinLevel <= level) pick = z;
            else break;
        }
        return pick.PlaceName;
    }
}
