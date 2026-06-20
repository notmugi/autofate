using AutoFates.Features;

namespace AutoFates.Core;

/// <summary>
/// Tracks per-session statistics. Crucially, the gemstone counter measures *gross* gemstones
/// gained from FATEs across the whole session and is NOT decremented when we auto-buy items at a
/// vendor. We do this by sampling the live inventory count each tick and only adding positive
/// deltas to the running total (spending shows as a negative delta, which we ignore).
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

    private int _lastGemstoneSample = -1;

    public void Reset()
    {
        StartedUtc = DateTime.UtcNow;
        FatesCompleted = 0;
        FatesAttempted = 0;
        GemstonesGained = 0;
        _lastGemstoneSample = InventoryUtil.GetGemstoneCount();
        StartLevel = ECommons.GameHelpers.Player.Level;
        CurrentLevel = StartLevel;
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
