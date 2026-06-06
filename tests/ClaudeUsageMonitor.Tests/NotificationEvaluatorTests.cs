using ClaudeUsageMonitor;
using Xunit;

namespace ClaudeUsageMonitor.Tests;

public class NotificationEvaluatorTests
{
    // Fixed future timestamp — ensures consecutive polls with the same resetsAt
    // don't accidentally look like a reset window advancing.
    private static readonly DateTime DefaultResetsAt = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static UsageData Session(double pct, DateTime? resetsAt = null) => new()
    {
        SessionPercent = pct,
        SessionResetsAt = resetsAt ?? DefaultResetsAt,
        HasWeekly = false,
    };

    private static readonly DateTime DefaultWeeklyResetsAt = new DateTime(2099, 1, 8, 0, 0, 0, DateTimeKind.Utc);

    private static UsageData SessionAndWeekly(
        double sessionPct, DateTime? sessionResetsAt,
        double weeklyPct, DateTime? weeklyResetsAt) => new()
    {
        SessionPercent = sessionPct,
        SessionResetsAt = sessionResetsAt ?? DefaultResetsAt,
        WeeklyPercent = weeklyPct,
        WeeklyResetsAt = weeklyResetsAt ?? DefaultWeeklyResetsAt,
        HasWeekly = true,
    };

    // 1. HighUsage fires once at 90
    [Fact]
    public void HighUsage_FiresOnce_ThenSilentOnRepeat()
    {
        var ev = new NotificationEvaluator();

        var first = ev.Evaluate(Session(91)).ToList();
        Assert.Single(first, e => e.Kind == NotifyKind.HighUsage);

        var second = ev.Evaluate(Session(91)).ToList();
        Assert.Empty(second);
    }

    // 2. HighUsage re-arms after drop below 90
    [Fact]
    public void HighUsage_ReArms_AfterDropBelow90()
    {
        var ev = new NotificationEvaluator();

        ev.Evaluate(Session(91)).ToList();   // arms
        ev.Evaluate(Session(88)).ToList();   // drops → resets warned flag
        var third = ev.Evaluate(Session(92)).ToList();

        Assert.Contains(third, e => e.Kind == NotifyKind.HighUsage);
    }

    // 3. LimitReached fires once at ~100
    [Fact]
    public void LimitReached_FiresOnce_AtThreshold()
    {
        var ev = new NotificationEvaluator();

        var first = ev.Evaluate(Session(99.5)).ToList();
        Assert.Contains(first, e => e.Kind == NotifyKind.LimitReached);

        var second = ev.Evaluate(Session(100)).ToList();
        Assert.DoesNotContain(second, e => e.Kind == NotifyKind.LimitReached);
    }

    // 4. LimitReached re-arms after drop
    [Fact]
    public void LimitReached_ReArms_AfterDrop()
    {
        var ev = new NotificationEvaluator();

        ev.Evaluate(Session(100)).ToList();   // fires + arms
        ev.Evaluate(Session(50)).ToList();    // drops → resets reached flag
        var third = ev.Evaluate(Session(99.6)).ToList();

        Assert.Contains(third, e => e.Kind == NotifyKind.LimitReached);
    }

    // 5. Reset detected when resetsAt advances
    [Fact]
    public void Reset_Detected_WhenResetsAtAdvances()
    {
        var ev = new NotificationEvaluator();
        var t1 = DateTime.UtcNow.AddHours(1);
        var t2 = t1.AddHours(5); // new window opened

        // First poll: no Reset (guard)
        var first = ev.Evaluate(Session(50, t1)).ToList();
        Assert.DoesNotContain(first, e => e.Kind == NotifyKind.Reset);

        // Second poll: resetsAt advanced → Reset
        var second = ev.Evaluate(Session(5, t2)).ToList();
        Assert.Contains(second, e => e.Kind == NotifyKind.Reset);
    }

    // 5b. After reset, warned90 and reached100 are cleared
    [Fact]
    public void Reset_ClearsWarningFlags()
    {
        var ev = new NotificationEvaluator();
        var t1 = DateTime.UtcNow.AddHours(1);
        var t2 = t1.AddHours(5);

        ev.Evaluate(Session(100, t1)).ToList(); // both flags armed
        ev.Evaluate(Session(5, t2)).ToList();   // reset → flags cleared

        // Should fire HighUsage again since flag was cleared
        var fourth = ev.Evaluate(Session(95, t2)).ToList();
        Assert.Contains(fourth, e => e.Kind == NotifyKind.HighUsage);
    }

    // 6. No Reset on first poll
    [Fact]
    public void NoReset_OnFirstPoll()
    {
        var ev = new NotificationEvaluator();

        var events = ev.Evaluate(Session(50, DateTime.UtcNow.AddHours(3))).ToList();

        Assert.DoesNotContain(events, e => e.Kind == NotifyKind.Reset);
    }

    // 7. HighUsage + LimitReached both fire in one poll (100% first call)
    [Fact]
    public void HighUsage_And_LimitReached_BothFire_InOnePoll()
    {
        var ev = new NotificationEvaluator();

        var events = ev.Evaluate(Session(100)).ToList();

        Assert.Contains(events, e => e.Kind == NotifyKind.HighUsage);
        Assert.Contains(events, e => e.Kind == NotifyKind.LimitReached);
    }

    // 8. Weekly events only if HasWeekly=true
    [Fact]
    public void Weekly_NoEvents_WhenHasWeeklyFalse()
    {
        var ev = new NotificationEvaluator();
        var data = new UsageData
        {
            SessionPercent = 10,
            SessionResetsAt = DefaultResetsAt,
            WeeklyPercent = 95,
            WeeklyResetsAt = DefaultWeeklyResetsAt,
            HasWeekly = false,
        };

        var events = ev.Evaluate(data).ToList();

        Assert.DoesNotContain(events, e => e.Quota == "7d weekly");
    }

    [Fact]
    public void Weekly_FiresHighUsage_WhenHasWeeklyTrue()
    {
        var ev = new NotificationEvaluator();
        var data = SessionAndWeekly(
            sessionPct: 10, sessionResetsAt: null,
            weeklyPct: 95, weeklyResetsAt: null);

        var events = ev.Evaluate(data).ToList();

        Assert.Contains(events, e => e.Kind == NotifyKind.HighUsage && e.Quota == "7d weekly");
    }

    // Quota labels
    [Fact]
    public void SessionEvents_HaveCorrectQuotaLabel()
    {
        var ev = new NotificationEvaluator();
        var events = ev.Evaluate(Session(95)).ToList();

        Assert.All(events, e => Assert.Equal("5h session", e.Quota));
    }

    // 9. Null resetsAt on first poll → guard fires once; subsequent real resetsAt → Reset detected
    [Fact]
    public void NullResetsAt_OnFirstPoll_ThenRealResetsAt_DetectsReset()
    {
        var ev = new NotificationEvaluator();
        var first = ev.Evaluate(new UsageData { SessionPercent = 50, SessionResetsAt = null }).ToList();
        Assert.DoesNotContain(first, e => e.Kind == NotifyKind.Reset);

        var t = DateTime.UtcNow.AddHours(2);
        var second = ev.Evaluate(new UsageData { SessionPercent = 5, SessionResetsAt = t }).ToList();
        Assert.Contains(second, e => e.Kind == NotifyKind.Reset);
    }
}
