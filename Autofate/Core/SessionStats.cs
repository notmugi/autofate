using Autofate.Features;

namespace Autofate.Core;

/// <summary>
/// Tracks per-session stats. Gemstone counter is gross (spending is ignored): we sample inventory
/// each tick and only add positive deltas.
/// </summary>
public sealed class SessionStats
{
    public DateTime StartedUtc { get; private set; } = DateTime.UtcNow;
    public TimeSpan Runtime => DateTime.UtcNow - StartedUtc;

    public int FatesCompleted { get; private set; }
    public int FatesAttempted { get; private set; }

    /// <summary>Gross bicolor gemstones earned this session (spending excluded).</summary>
    public int GemstonesGained { get; private set; }

    public int StartLevel { get; private set; }
    public int CurrentLevel { get; set; }

    /// <summary>Number of times the player has died this session.</summary>
    public int Deaths { get; private set; }

    private int _lastGemstoneSample = -1;
    private bool _wasDead;

    public void Reset()
    {
        StartedUtc = DateTime.UtcNow;
        FatesCompleted = 0;
        FatesAttempted = 0;
        GemstonesGained = 0;
        Deaths = 0;
        _wasDead = false;
        _lastGemstoneSample = InventoryUtil.GetGemstoneCount();
        StartLevel = ECommons.GameHelpers.Player.Level;
        CurrentLevel = StartLevel;
    }

    /// <summary>Sample player HP; count a death on the transition into the dead state (rising edge).</summary>
    public void SampleDeaths()
    {
        var me = ECommons.GameHelpers.Player.Object;
        var dead = me != null && me.IsDead;
        if (dead && !_wasDead) Deaths++;
        _wasDead = dead;
    }

    /// <summary>Sample the gemstone inventory; add only positive deltas to the gross total.</summary>
    public void SampleGemstones()
    {
        var now = InventoryUtil.GetGemstoneCount();
        if (_lastGemstoneSample < 0)
        {
            _lastGemstoneSample = now;
            return;
        }
        var delta = now - _lastGemstoneSample;
        if (delta > 0) GemstonesGained += delta;
        _lastGemstoneSample = now;
    }

    /// <summary>Call when a fate is finished (threshold met / completed).</summary>
    public void OnFateCompleted() => FatesCompleted++;
    public void OnFateAttempted() => FatesAttempted++;
}
