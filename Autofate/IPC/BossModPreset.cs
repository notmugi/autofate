namespace Autofate.IPC;

/// <summary>
/// Autofate's OWN BossMod/BMR autorotation preset, tuned for FATE farming. We CREATE this preset in
/// BossMod via IPC (Presets.Create) so the user never has to make one by hand.
///
/// This is OUR preset — authored here, not copied from any other plugin. The module TYPE NAMES
/// (e.g. "BossMod.Autorotation.xan.PLD") are BossMod's own rotation-module classes and MUST be
/// referenced by their real type names or BossMod can't load them; the curation (which modules,
/// which tracks/options) is entirely our own choosing. Module names are identical across plain
/// BossMod and BossMod Reborn, so this works on either fork.
///
/// Design choices (our own, informed by what works well for fates):
///   - MiscAI.AutoTarget: General=Aggressive (auto-pick targets), Retarget=Hostiles (STICKY — only
///     switch off the current mob if it's an ally/dead, never thrash between mobs). FATE scoping +
///     MaxTargets are pushed as TRANSIENT strategies at runtime (ApplyFateTargeting) so they track
///     our config live; they are NOT baked here.
///   - MiscAI.NormalMovement: Destination=Pathfind — obstacle-aware navigation to the target (only
///     used when BMR is also the MOVEMENT backend; harmless otherwise).
///   - Every job module with Targeting=Auto, AOE=AOE, and Buffs=Automatic — the Buffs=Automatic bit
///     means burst windows fire on their own, which clears fate mobs noticeably faster. (BLU is the
///     odd one out and only gets Targeting=Auto.)
///   - Role AI helpers (Healer/Melee/Ranged/Tank/Caster) for mitigation, utility, and raises.
///
/// We deliberately do NOT include MiscAI.FateUtils (sync / hand-in / collect / chocobo): Autofate
/// drives level-sync, collect turn-ins, and chocobo handling itself, so letting the rotation also
/// do them would double-drive those systems.
/// </summary>
internal static class BossModPreset
{
    /// <summary>Our preset name. Fixed — Autofate always creates/activates this preset.</summary>
    public const string Name = "Autofate";

    // Standard jobs get the three shared tracks. Targeting=Auto picks the best AOE target,
    // AOE=AOE uses AOE rotations, Buffs=Automatic auto-fires burst for faster clears.
    private static readonly string[] StandardJobs =
    {
        "PLD", "WAR", "DRK", "GNB",                       // tanks (WAR handled separately, see below)
        "WHM", "SCH", "AST", "SGE",                       // healers
        "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",         // melee
        "BRD", "MCH", "DNC",                              // ranged phys
        "BLM", "SMN", "RDM", "PCT",                       // casters
    };

    /// <summary>
    /// Serialized preset JSON. {0} is substituted with the preset name so a renamed preset still
    /// gets the same module set under the chosen name.
    /// </summary>
    public static string Json(string presetName)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('{').Append('\n');
        sb.Append("  \"Name\": \"").Append(presetName).Append("\",\n");
        sb.Append("  \"Modules\": {\n");

        // --- AI targeting (sticky, auto-aggressive) ---
        sb.Append("    \"BossMod.Autorotation.MiscAI.AutoTarget\": [\n");
        sb.Append("      { \"Track\": \"General\", \"Option\": \"Aggressive\" },\n");
        sb.Append("      { \"Track\": \"Retarget\", \"Option\": \"Hostiles\" }\n");
        sb.Append("    ],\n");

        // --- obstacle-aware movement (only used if BMR owns movement) ---
        sb.Append("    \"BossMod.Autorotation.MiscAI.NormalMovement\": [\n");
        sb.Append("      { \"Track\": \"Destination\", \"Option\": \"Pathfind\" }\n");
        sb.Append("    ],\n");

        // --- per-job rotations ---
        foreach (var job in StandardJobs)
        {
            if (job == "WAR")
            {
                // WAR's module lives in a different namespace (BossMod.Autorotation.VeynWAR) and uses
                // its own track names, not the shared xan tracks.
                sb.Append("    \"BossMod.Autorotation.VeynWAR\": [\n");
                sb.Append("      { \"Track\": \"AOE\", \"Option\": \"AutoFinishCombo\" },\n");
                sb.Append("      { \"Track\": \"Burst\", \"Option\": \"Spend\" }\n");
                sb.Append("    ],\n");
                continue;
            }
            sb.Append("    \"BossMod.Autorotation.xan.").Append(job).Append("\": [\n");
            sb.Append("      { \"Track\": \"Targeting\", \"Option\": \"Auto\" },\n");
            sb.Append("      { \"Track\": \"AOE\", \"Option\": \"AOE\" },\n");
            sb.Append("      { \"Track\": \"Buffs\", \"Option\": \"Automatic\" }\n");
            sb.Append("    ],\n");
        }

        // BLU: only auto-targeting (no shared AOE/Buffs tracks).
        sb.Append("    \"BossMod.Autorotation.xan.BLU\": [\n");
        sb.Append("      { \"Track\": \"Targeting\", \"Option\": \"Auto\" }\n");
        sb.Append("    ],\n");

        // --- role AI helpers (mitigation / utility / raise) ---
        sb.Append("    \"BossMod.Autorotation.xan.HealerAI\": [\n");
        sb.Append("      { \"Track\": \"Heal\", \"Option\": \"Enabled\" },\n");
        sb.Append("      { \"Track\": \"Esuna2\", \"Option\": \"Enabled\" },\n");
        sb.Append("      { \"Track\": \"Raise\", \"Option\": \"Slowcast\" }\n");
        sb.Append("    ],\n");
        sb.Append("    \"BossMod.Autorotation.xan.MeleeAI\": [\n");
        sb.Append("      { \"Track\": \"Second Wind\", \"Option\": \"Enabled\" },\n");
        sb.Append("      { \"Track\": \"Bloodbath\", \"Option\": \"Enabled\" }\n");
        sb.Append("    ],\n");
        sb.Append("    \"BossMod.Autorotation.xan.RangedAI\": [\n");
        sb.Append("      { \"Track\": \"Second Wind\", \"Option\": \"Enabled\" }\n");
        sb.Append("    ],\n");
        sb.Append("    \"BossMod.Autorotation.xan.TankAI\": [\n");
        sb.Append("      { \"Track\": \"Stance\", \"Option\": \"Enabled\" },\n");
        sb.Append("      { \"Track\": \"Personal mits\", \"Option\": \"Enabled\" }\n");
        sb.Append("    ],\n");
        sb.Append("    \"BossMod.Autorotation.xan.Caster\": [\n");
        sb.Append("      { \"Track\": \"Raise\", \"Option\": \"Swiftcast\" }\n");
        sb.Append("    ]\n");

        sb.Append("  }\n");
        sb.Append('}').Append('\n');
        return sb.ToString();
    }
}
