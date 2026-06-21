namespace Autofate.Data;

/// <summary>Well-known item ids used by the plugin (verified via Garland Tools).</summary>
public static class GameItems
{
    public const uint GysahlGreens = 4868;        // resets chocobo companion timer
    public const uint CurielRoot = 7894;          // chocobo training (rank xp)
    public const uint ThavnairianOnion = 8166;    // chocobo training past rank 10
    public const uint MagickedStableBroom = 8168; // clean FC chocobo stable

    public const uint BicolorGemstone = 26807;    // currency item id for bicolor gemstones

    // Dark matter grades for self-repair (Grade 1..8); Grade 8 is the usual cap.
    public static readonly uint[] DarkMatter =
    {
        5594,   // Grade 1 Dark Matter
        5595,   // Grade 2
        5596,   // Grade 3
        5597,   // Grade 4
        5598,   // Grade 5
        10386,  // Grade 6
        17837,  // Grade 7
        33916,  // Grade 8
    };
}
