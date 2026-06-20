namespace AutoFates;

public enum FarmingMode
{
    Leveling,
    SingleZone,
    SharedFates,
    Atma,
    Demiatma,
    LuminousCrystals,
    Memories,
    Manual,
}

[Flags]
public enum FateType
{
    None = 0,
    Battle = 1 << 0,
    Boss = 1 << 1,
    Collect = 1 << 2,
    Defend = 1 << 3,
    Escort = 1 << 4,
    All = Battle | Boss | Collect | Defend | Escort,
}

public enum ChocoboStance
{
    Follow = 0,   // not summoned / default
    Defender = 1,
    Attacker = 2,
    Healer = 3,
}

public enum CombatBackend
{
    None = 0,
    BossModReborn = 1,
    WrathCombo = 2,
    RotationSolverReborn = 3,
}

public enum RepairMode
{
    SelfRepair = 0,
    NpcRepair = 1,
}

/// <summary>High level state of the farming engine.</summary>
public enum FarmState
{
    Idle,
    SelectingZone,
    TravelingToZone,
    SelectingFate,
    TravelingToFate,
    InFate,
    ClearingAggro,     // kill stray non-fate enemies attacking us before moving on
    CollectTurnIn,
    Maintenance,       // repair / consumables / retainer
    ChocoboLeveling,
    GemstoneShopping,
    Finishing,         // lifestream to final destination
    Stopped,
}

/// <summary>What kind of automatic stop happened, for reporting.</summary>
public enum StopReason
{
    None,
    UserRequested,
    LevelReached,
    GemstoneCountReached,
    ChocoboMaxed,
    VendorRequirementMet,
    OutOfDarkMatter,
    AllSharedFatesMaxed,
    Error,
}
