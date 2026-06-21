using ECommons.Configuration;

namespace AutoFates;

#pragma warning disable CS0618 // EzConfig works with any plain class

/// <summary>A single user-defined zone entry for Manual mode.</summary>
public sealed class ManualZoneEntry
{
    public uint TerritoryId;
    public string Name = string.Empty;
    /// <summary>How many fates to complete in this zone before moving on. 0 = unlimited.</summary>
    public int FatesToRun = 5;
    /// <summary>Live counter of fates done in this zone this session.</summary>
    public int FatesDone;

    public void ResetCounter() => FatesDone = 0;
}

/// <summary>A bicolor gemstone shop buy-list entry.</summary>
public sealed class GemstoneBuyEntry
{
    public uint ItemId;
    public string Name = string.Empty;
    /// <summary>Stop buying once we own this many. 0 = buy continuously while farming.</summary>
    public int TargetQuantity;
    public bool Enabled = true;
}

public sealed class Configuration
{
    public int Version { get; set; } = 1;

    // ------------------------------------------------------------------ Mode
    public FarmingMode Mode = FarmingMode.Atma;

    // Single zone
    public uint SingleZoneTerritory;

    // Manual mode
    public List<ManualZoneEntry> ManualZones = new();
    public bool ManualLoop = true;

    // ------------------------------------------------------------------ Navigation / mount
    /// <summary>Use a mount for long-distance travel via vnavmesh.</summary>
    public bool UseMount = true;
    /// <summary>Fly when the zone allows it (flight unlocked).</summary>
    public bool UseFlight = true;
    /// <summary>Preferred mount id (0 = Mount Roulette / first available).</summary>
    public uint PreferredMountId;
    public string PreferredMountName = "Mount Roulette";
    /// <summary>Distance (yalms) beyond which we bother mounting before traveling.</summary>
    public float MountDistanceThreshold = 20f;

    // ------------------------------------------------------------------ Fate engine
    public FateType EnabledFateTypes = FateType.All;
    /// <summary>Continue running fates up to this many levels above the player's level.</summary>
    public int LevelsAbovePlayer = 2;
    /// <summary>Ignore fates with less than this many seconds remaining.</summary>
    public int MinFateTimeSeconds = 120;
    /// <summary>Prefer fates that are lower on their timer over closer ones.</summary>
    public bool PrioritizeLowTimer = true;
    /// <summary>Sync level to the fate we travel to (and avoid syncing to pass-through fates).</summary>
    public bool AutoLevelSync = true;
    /// <summary>Pull every enemy in the fate area.</summary>
    public bool MassPull = false;
    /// <summary>Keep this distance (yalms) from non-fate enemies to avoid aggro.</summary>
    public float SafeDistance = 8f;
    /// <summary>Self AOE dodging when not using BMR for movement.</summary>
    public bool AutoDodgeAoe = true;

    // Collect fate tuning
    public int CollectInitialTurnIn = 5;

    /// <summary>
    /// How long (seconds) to stay in a zone waiting for FATEs to (re)spawn before rotating to
    /// another zone. FATEs pop every few minutes, so we dwell rather than zone-hop the instant
    /// there's no active FATE. Only applies to multi-zone modes. 0 = never rotate (stay forever).
    /// </summary>
    public int ZoneDwellSeconds = 240;

    // ------------------------------------------------------------------ Shared FATEs
    /// <summary>Include Shadowbringers zones in Shared FATEs mode.</summary>
    public bool SharedFateShB = true;
    /// <summary>Include Endwalker zones in Shared FATEs mode.</summary>
    public bool SharedFateEW = true;
    /// <summary>Include Dawntrail zones in Shared FATEs mode.</summary>
    public bool SharedFateDT = true;
    /// <summary>In Shared FATEs mode, skip zones whose shared-fate rank is already maxed (read from the in-game tracker).</summary>
    public bool SharedFateSkipMaxed = true;
    /// <summary>Stop farming entirely once every shared-fate zone is maxed.</summary>
    public bool StopWhenAllSharedFatesMaxed = false;

    // ------------------------------------------------------------------ Combat backends
    // Default to vnavmesh-only movement (AutoFates drives targeting + positioning itself). This is
    // the confirmed-working combo: navmesh handles travel, AutoFates walks into range, and the
    // rotation backend (Wrath/RSR/BMR) does the damage. BMR-AI movement is opt-in.
    public CombatBackend MovementBackend = CombatBackend.None;          // movement + AOE
    public CombatBackend RotationBackend = CombatBackend.WrathCombo;    // damage rotation
    public string BmrPreset = "AutoFates"; // BMR preset to activate

    // ------------------------------------------------------------------ Party follow
    public bool FollowPartyLeader = false;
    public float FollowDistance = 3f;

    // ------------------------------------------------------------------ Leveling / stop triggers
    public int DesiredLevel = 100;                 // stop leveling at this level
    public bool StopAtLevel = false;
    public bool StopAtGemstoneCount = false;
    public int GemstoneStopCount = 1000;
    public bool StopAtChocoboMaxed = false;
    public bool StopAtVendorRequirementMet = false;

    // ------------------------------------------------------------------ Lifestream
    public bool UseLifestream = true;
    /// <summary>Lifestream command to run when farming completes (e.g. "/li home").</summary>
    public string LifestreamFinishCommand = string.Empty;
    public bool LifestreamOnFinish = false;

    // ------------------------------------------------------------------ Chocobo
    public bool ChocoboCompanionEnabled = true;
    public ChocoboStance ChocoboStance = ChocoboStance.Attacker;
    public bool AutoHealerStance = true;
    public int HealerStanceHpThreshold = 50; // % HP to switch to healer
    public bool AutoGysahlGreens = true;
    public int GysahlReuseSeconds = 60; // re-summon when companion timer below this

    // Chocobo leveling
    public bool ChocoboLevelingEnabled = false;
    public int ChocoboTargetLevel = 20;
    /// <summary>Home destination type for stabling: 0 FC, 1 Private, 2 Apartment, 3 Shared.</summary>
    public int ChocoboHomeType = 0;
    public string ChocoboHomeLifestreamCommand = "/li home";
    public System.Numerics.Vector3 StablePosition;
    public uint StableTerritory;
    public bool StablePositionSet = false;
    /// <summary>DataId of the stable entity the user targeted + added (0 = not captured). Lets us
    /// find the exact stable object by type rather than guessing by name.</summary>
    public uint StableDataId;
    /// <summary>Display name of the captured stable entity (for the UI).</summary>
    public string StableName = string.Empty;
    public bool AutoCleanStable = true;
    /// <summary>Persisted UTC of the last stable/train (the ~1h cooldown). Survives reloads so we
    /// don't immediately re-stable after fetching the chocobo back out.</summary>
    public long LastStableUnixMs = 0;

    // ------------------------------------------------------------------ Consumables
    public bool UseFood = false;
    public uint FoodItemId;
    public bool FoodIsHq;
    public bool UsePotion = false;
    public uint PotionItemId;
    public bool PotionIsHq;
    /// <summary>Re-apply food/pot when remaining time drops below this many seconds.</summary>
    public int ConsumableReuseSeconds = 60;

    // ------------------------------------------------------------------ Repair
    public bool AutoRepair = true;
    public int RepairThresholdPercent = 30;
    public RepairMode RepairMode = RepairMode.SelfRepair;

    // ------------------------------------------------------------------ Retainer / saddlebag
    public bool UseRetainerStorage = false;
    public bool UseSaddlebag = false;
    public List<uint> AutoStoreItemIds = new();

    // ------------------------------------------------------------------ Gemstone shop
    public bool EnableGemstoneShopping = false;
    public List<GemstoneBuyEntry> GemstoneBuyList = new();
    /// <summary>Open the vendor once we hold at least this many gemstones.</summary>
    public int GemstoneBuyThreshold = 1000;
    /// <summary>Which gemstone vendor NPC to buy from (place name / npc identifier).</summary>
    public string GemstoneVendor = "Gemstone Trader";
    // Captured vendor location (target the vendor NPC in-game, then "Add targeted vendor").
    /// <summary>DataId (BaseId) of the captured vendor NPC (0 = not captured). Used to find the exact NPC to interact with.</summary>
    public uint VendorDataId;
    /// <summary>Display name of the captured vendor NPC (for the UI).</summary>
    public string VendorName = string.Empty;
    /// <summary>World position of the captured vendor NPC, for vnav navigation.</summary>
    public System.Numerics.Vector3 VendorPosition;
    /// <summary>Territory the vendor NPC lives in (we teleport here before navigating to it).</summary>
    public uint VendorTerritory;
    public bool VendorPositionSet = false;

    // ------------------------------------------------------------------ UI / misc
    public bool VerboseLogging = false;
    public bool OpenOnLogin = false;

    /// <summary>ExVersion row ids for the shared-fate expansions the user has enabled (3=ShB,4=EW,5=DT).</summary>
    public IEnumerable<uint> SelectedSharedFateExpansions()
    {
        if (SharedFateShB) yield return 3;
        if (SharedFateEW) yield return 4;
        if (SharedFateDT) yield return 5;
    }

    public void Save() => EzConfig.Save();
}
