using ClaudeUsageMonitor;
using Xunit;

namespace ClaudeUsageMonitor.Tests;

public class BurnRateTrackerTests
{
    private static DateTime T(int minutesFromBase) =>
        new DateTime(2099, 6, 1, 12, 0, 0, DateTimeKind.Utc).AddMinutes(minutesFromBase);

    // 1. Fewer than 2 samples → RatePerHour is null.
    [Fact]
    public void RatePerHour_Null_WhenFewerThanTwoSamples()
    {
        var tracker = new BurnRateTracker();
        Assert.Null(tracker.RatePerHour);

        tracker.AddSample(T(0), 10.0);
        Assert.Null(tracker.RatePerHour);
    }

    // 2. Two samples less than 10 minutes apart → null (span too short).
    [Fact]
    public void RatePerHour_Null_WhenSpanLessThan10Minutes()
    {
        var tracker = new BurnRateTracker();
        tracker.AddSample(T(0), 10.0);
        tracker.AddSample(T(9), 12.0);  // 9 min apart — below MinSpan

        Assert.Null(tracker.RatePerHour);
    }

    // 3. Normal rate: two samples ≥10 min apart.
    [Fact]
    public void RatePerHour_CorrectValue_NormalSamples()
    {
        var tracker = new BurnRateTracker();
        tracker.AddSample(T(0),  10.0);
        tracker.AddSample(T(60), 20.0);  // +10% in 60 min → 10%/h

        Assert.NotNull(tracker.RatePerHour);
        Assert.Equal(10.0, tracker.RatePerHour!.Value, precision: 4);
    }

    // 4. EstimateToFull respects the rate.
    [Fact]
    public void EstimateToFull_CorrectDuration()
    {
        var tracker = new BurnRateTracker();
        tracker.AddSample(T(0),  10.0);
        tracker.AddSample(T(60), 20.0);  // rate = 10%/h; at 20% → 80% remaining → 8h

        var eta = tracker.EstimateToFull(20.0);
        Assert.NotNull(eta);
        Assert.Equal(TimeSpan.FromHours(8), eta!.Value);
    }

    // 5. Significant pct drop clears samples (reset detected).
    [Fact]
    public void AddSample_ClearsSamples_OnSignificantDrop()
    {
        var tracker = new BurnRateTracker();
        tracker.AddSample(T(0),   10.0);
        tracker.AddSample(T(60),  80.0);

        // Drop of >10% → samples cleared; rate must be null after single post-drop sample.
        tracker.AddSample(T(65), 5.0);  // drop of 75% — reset

        Assert.Null(tracker.RatePerHour);
    }

    // 6. Samples older than 60 minutes are pruned from the window.
    [Fact]
    public void PruneOld_DropsOutOfWindowSamples()
    {
        var tracker = new BurnRateTracker();
        // Add two samples 70 minutes apart; after the third sample at T(70),
        // the T(0) sample is more than 60 min behind T(70) → pruned.
        tracker.AddSample(T(0),  10.0);
        tracker.AddSample(T(70), 17.0);  // T(0) is now 70 min old → pruned

        // After pruning, only one sample remains → rate is null.
        Assert.Null(tracker.RatePerHour);
    }

    // 7. EstimateToFull returns null when rate is non-positive.
    [Fact]
    public void EstimateToFull_Null_WhenRateZeroOrNegative()
    {
        var tracker = new BurnRateTracker();
        tracker.AddSample(T(0),  50.0);
        tracker.AddSample(T(60), 50.0);  // flat — rate = 0

        Assert.Null(tracker.EstimateToFull(50.0));
    }
}
