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

    // Regression: API resets_at 1-second jitter must not fire Reset
    [Fact]
    public void Reset_NotFired_OnSubMinuteJitter()
    {
        var ev = new NotificationEvaluator();
        var t0 = new DateTime(2099, 3, 1, 2, 0, 0, DateTimeKind.Utc);
        var t1 = t0.AddSeconds(1);  // jitter forward
        var t2 = t0;                // jitter back

        // First poll: guard (no Reset)
        ev.Evaluate(Session(50, t0)).ToList();

        // Jitter forward by 1 second — must NOT fire Reset (delta = 1s < 1 min)
        var second = ev.Evaluate(Session(55, t1)).ToList();
        Assert.DoesNotContain(second, e => e.Kind == NotifyKind.Reset);

        // Jitter back — must NOT fire Reset
        var third = ev.Evaluate(Session(60, t2)).ToList();
        Assert.DoesNotContain(third, e => e.Kind == NotifyKind.Reset);

        // Real reset: hours later — MUST fire Reset
        var t3 = t0.AddHours(5);
        var fourth = ev.Evaluate(Session(5, t3)).ToList();
        Assert.Contains(fourth, e => e.Kind == NotifyKind.Reset);
    }

    // Configurable threshold: high usage fires at a custom value
    [Fact]
    public void HighUsage_FiresAtCustomThreshold()
    {
        var ev = new NotificationEvaluator { HighUsageThreshold = 75.0 };

        var below = ev.Evaluate(Session(74)).ToList();
        Assert.DoesNotContain(below, e => e.Kind == NotifyKind.HighUsage);

        var at = ev.Evaluate(Session(76)).ToList();
        Assert.Contains(at, e => e.Kind == NotifyKind.HighUsage);
    }

    // Default threshold remains 90 when not configured
    [Fact]
    public void HighUsage_DefaultThresholdIs90()
    {
        var ev = new NotificationEvaluator();

        var below = ev.Evaluate(Session(89)).ToList();
        Assert.DoesNotContain(below, e => e.Kind == NotifyKind.HighUsage);

        var at = ev.Evaluate(Session(90)).ToList();
        Assert.Contains(at, e => e.Kind == NotifyKind.HighUsage);
    }

    // Bug 3a: Local-timer reset — Reset fires as soon as resetsAt moment is in the past,
    //         but only after a baseline has been established by the first poll.
    [Fact]
    public void Reset_FiresLocallyWhenResetsAtPassed_ExactlyOnce()
    {
        var fakeClock = new DateTime(2099, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var resetsAt  = fakeClock.AddMinutes(5);   // still in the future at first poll

        var ev = new NotificationEvaluator { UtcNow = () => fakeClock };

        // First poll: resetsAt is still in the future → no Reset, but baseline is set.
        var first = ev.Evaluate(Session(5, resetsAt)).ToList();
        Assert.DoesNotContain(first, e => e.Kind == NotifyKind.Reset);

        // Advance clock past resetsAt.
        fakeClock = resetsAt.AddSeconds(10);
        ev.UtcNow = () => fakeClock;

        // Second poll: same resetsAt, now in the past → local-timer Reset fires once.
        var second = ev.Evaluate(Session(5, resetsAt)).ToList();
        Assert.Contains(second, e => e.Kind == NotifyKind.Reset);

        // Third poll with the same resetsAt: must NOT fire again (dedupe).
        var third = ev.Evaluate(Session(5, resetsAt)).ToList();
        Assert.DoesNotContain(third, e => e.Kind == NotifyKind.Reset);
    }

    // Bug 3b: After local-timer reset fires, repeated polls carrying the same past
    //         resetsAt must NOT fire Reset again (dedupe guard).
    [Fact]
    public void Reset_LocalTimer_DoesNotFireAgain_OnRepeatPollWithSameResetsAt()
    {
        var fakeClock = new DateTime(2099, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var resetsAt  = fakeClock.AddMinutes(5);

        var ev = new NotificationEvaluator { UtcNow = () => fakeClock };

        // First poll: resetsAt still in the future → establishes baseline, no Reset.
        var first = ev.Evaluate(Session(5, resetsAt)).ToList();
        Assert.DoesNotContain(first, e => e.Kind == NotifyKind.Reset);

        // Advance clock past resetsAt.
        fakeClock = resetsAt.AddSeconds(10);
        ev.UtcNow = () => fakeClock;

        // Second poll after elapsed: local-timer Reset fires.
        var second = ev.Evaluate(Session(5, resetsAt)).ToList();
        Assert.Contains(second, e => e.Kind == NotifyKind.Reset);

        // Next poll arrives with the same resetsAt (API hasn't switched windows yet).
        // Must NOT fire Reset a second time.
        var third = ev.Evaluate(Session(5, resetsAt)).ToList();
        Assert.DoesNotContain(third, e => e.Kind == NotifyKind.Reset);

        // Fourth poll: same resetsAt, same result.
        var fourth = ev.Evaluate(Session(5, resetsAt)).ToList();
        Assert.DoesNotContain(fourth, e => e.Kind == NotifyKind.Reset);
    }

    // Regression: first poll with a past resetsAt must NOT produce a spurious Reset.
    // Before the fix, the local-timer path ran even with no baseline (_lastResetsAt == null),
    // causing a false Reset notification on startup.
    [Fact]
    public void NoSpuriousReset_OnFirstPollWithPastResetsAt()
    {
        var ev = new NotificationEvaluator();
        var pastResetsAt = DateTime.UtcNow.AddSeconds(-10);

        var events = ev.Evaluate(Session(5, pastResetsAt)).ToList();

        Assert.DoesNotContain(events, e => e.Kind == NotifyKind.Reset);
    }

    // ── DepletionSoon tests ───────────────────────────────────────────────────

    private static DateTime FutureClock => new DateTime(2000, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // ETA < remaining time + pct>=50 → fires once
    [Fact]
    public void DepletionSoon_Fires_WhenEtaLessThanRemaining_AndPctAbove50()
    {
        var now = FutureClock;
        var resetsAt = now.AddHours(2);
        var eta = TimeSpan.FromHours(1); // hits limit in 1h, resets in 2h

        var ev = new NotificationEvaluator { UtcNow = () => now };
        ev.Evaluate(Session(60, resetsAt)).ToList(); // establish baseline

        var result = ev.Evaluate(Session(60, resetsAt), eta).ToList();
        Assert.Contains(result, e => e.Kind == NotifyKind.DepletionSoon);
    }

    // Same window: DepletionSoon fires only once
    [Fact]
    public void DepletionSoon_FiresOnlyOnce_PerWindow()
    {
        var now = FutureClock;
        var resetsAt = now.AddHours(2);
        var eta = TimeSpan.FromHours(1);

        var ev = new NotificationEvaluator { UtcNow = () => now };
        ev.Evaluate(Session(60, resetsAt)).ToList(); // establish baseline

        var first = ev.Evaluate(Session(60, resetsAt), eta).ToList();
        Assert.Contains(first, e => e.Kind == NotifyKind.DepletionSoon);

        var second = ev.Evaluate(Session(65, resetsAt), eta).ToList();
        Assert.DoesNotContain(second, e => e.Kind == NotifyKind.DepletionSoon);
    }

    // pct < 50 → does not fire
    [Fact]
    public void DepletionSoon_DoesNotFire_WhenPctBelow50()
    {
        var now = FutureClock;
        var resetsAt = now.AddHours(2);
        var eta = TimeSpan.FromHours(1);

        var ev = new NotificationEvaluator { UtcNow = () => now };
        ev.Evaluate(Session(40, resetsAt)).ToList();

        var result = ev.Evaluate(Session(40, resetsAt), eta).ToList();
        Assert.DoesNotContain(result, e => e.Kind == NotifyKind.DepletionSoon);
    }

    // ETA >= remaining → does not fire
    [Fact]
    public void DepletionSoon_DoesNotFire_WhenEtaGreaterThanRemaining()
    {
        var now = FutureClock;
        var resetsAt = now.AddHours(1);
        var eta = TimeSpan.FromHours(2); // hits limit in 2h, but resets in 1h — fine

        var ev = new NotificationEvaluator { UtcNow = () => now };
        ev.Evaluate(Session(60, resetsAt)).ToList();

        var result = ev.Evaluate(Session(60, resetsAt), eta).ToList();
        Assert.DoesNotContain(result, e => e.Kind == NotifyKind.DepletionSoon);
    }

    // After reset (new window), re-arms and fires again
    [Fact]
    public void DepletionSoon_ReArms_AfterReset()
    {
        var now = FutureClock;
        var resetsAt1 = now.AddHours(2);
        var eta = TimeSpan.FromHours(1);

        var ev = new NotificationEvaluator { UtcNow = () => now };
        ev.Evaluate(Session(60, resetsAt1)).ToList(); // baseline

        var first = ev.Evaluate(Session(60, resetsAt1), eta).ToList();
        Assert.Contains(first, e => e.Kind == NotifyKind.DepletionSoon);

        // New window arrives (API-driven reset)
        var resetsAt2 = resetsAt1.AddHours(5);
        ev.Evaluate(Session(5, resetsAt2)).ToList(); // reset clears flag

        var second = ev.Evaluate(Session(60, resetsAt2), eta).ToList();
        Assert.Contains(second, e => e.Kind == NotifyKind.DepletionSoon);
    }

    // sessionEtaToFull null → never fires
    [Fact]
    public void DepletionSoon_DoesNotFire_WhenEtaIsNull()
    {
        var now = FutureClock;
        var resetsAt = now.AddHours(2);

        var ev = new NotificationEvaluator { UtcNow = () => now };
        ev.Evaluate(Session(60, resetsAt)).ToList();

        var result = ev.Evaluate(Session(60, resetsAt)).ToList(); // no eta param
        Assert.DoesNotContain(result, e => e.Kind == NotifyKind.DepletionSoon);
    }

    // EtaToFull is populated in the event payload
    [Fact]
    public void DepletionSoon_Event_CarriesEtaToFull()
    {
        var now = FutureClock;
        var resetsAt = now.AddHours(2);
        var eta = TimeSpan.FromMinutes(32);

        var ev = new NotificationEvaluator { UtcNow = () => now };
        ev.Evaluate(Session(60, resetsAt)).ToList();

        var result = ev.Evaluate(Session(60, resetsAt), eta).ToList();
        var depEvent = result.FirstOrDefault(e => e.Kind == NotifyKind.DepletionSoon);
        Assert.NotNull(depEvent);
        Assert.Equal(eta, depEvent!.EtaToFull);
    }

    // ETA fluctuates (value → null → value): flag must be preserved; second fire must NOT happen
    [Fact]
    public void DepletionSoon_NoFlapping_WhenEtaFluctuates()
    {
        var now = FutureClock;
        var resetsAt = now.AddHours(2);
        var eta = TimeSpan.FromHours(1);

        var ev = new NotificationEvaluator { UtcNow = () => now };
        ev.Evaluate(Session(60, resetsAt)).ToList(); // establish baseline

        // First call with ETA: fires and sets _warnedDepletion
        var first = ev.Evaluate(Session(60, resetsAt), eta).ToList();
        Assert.Contains(first, e => e.Kind == NotifyKind.DepletionSoon);

        // ETA disappears (null): flag must remain set — no second fire possible
        var second = ev.Evaluate(Session(60, resetsAt)).ToList();
        Assert.DoesNotContain(second, e => e.Kind == NotifyKind.DepletionSoon);

        // ETA reappears: flag still set — must NOT fire again
        var third = ev.Evaluate(Session(65, resetsAt), eta).ToList();
        Assert.DoesNotContain(third, e => e.Kind == NotifyKind.DepletionSoon);
    }

    // Reset and DepletionSoon must not fire on the same Evaluate call
    [Fact]
    public void DepletionSoon_DoesNotFire_OnSameTurnAsReset()
    {
        var now = FutureClock;
        var resetsAt1 = now.AddHours(2);
        var eta = TimeSpan.FromHours(1);

        var ev = new NotificationEvaluator { UtcNow = () => now };
        ev.Evaluate(Session(60, resetsAt1)).ToList(); // establish baseline

        // API-driven reset: new resetsAt window + conditions that would normally trigger DepletionSoon
        var resetsAt2 = resetsAt1.AddHours(5);
        var resetTurn = ev.Evaluate(Session(60, resetsAt2), eta).ToList();

        Assert.Contains(resetTurn, e => e.Kind == NotifyKind.Reset);
        Assert.DoesNotContain(resetTurn, e => e.Kind == NotifyKind.DepletionSoon);

        // Next call (new window, same conditions): DepletionSoon now fires
        var nextTurn = ev.Evaluate(Session(60, resetsAt2), eta).ToList();
        Assert.Contains(nextTurn, e => e.Kind == NotifyKind.DepletionSoon);
    }

    // ── Cooldown tests ────────────────────────────────────────────────────────

    // Helper: builds a session UsageData with a resetsAt far in the future relative to the
    // fake clock used in cooldown tests (clock starts at 2000-01-01, resetsAt at 2099-01-01).
    private static UsageData CooldownSession(double pct) => Session(pct); // DefaultResetsAt = 2099

    // With default cooldown=0, HighUsage fires only once per window.
    [Fact]
    public void Cooldown_Zero_FiresOnlyOnce()
    {
        // Fake clock well before DefaultResetsAt (2099-01-01) so local-timer reset never triggers.
        var now = new DateTime(2000, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var ev  = new NotificationEvaluator { UtcNow = () => now, HighUsageCooldownMinutes = 0 };

        var first  = ev.Evaluate(CooldownSession(91)).ToList();
        var second = ev.Evaluate(CooldownSession(92)).ToList();
        var third  = ev.Evaluate(CooldownSession(93)).ToList();

        Assert.Single(first, e => e.Kind == NotifyKind.HighUsage);
        Assert.DoesNotContain(second, e => e.Kind == NotifyKind.HighUsage);
        Assert.DoesNotContain(third, e => e.Kind == NotifyKind.HighUsage);
    }

    // With cooldown>0, HighUsage re-fires after the cooldown period elapses.
    [Fact]
    public void Cooldown_RefiresAfterCooldownElapsed()
    {
        var now = new DateTime(2000, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var ev  = new NotificationEvaluator { UtcNow = () => now, HighUsageCooldownMinutes = 30 };

        // First crossing: fires immediately.
        var first = ev.Evaluate(CooldownSession(91)).ToList();
        Assert.Single(first, e => e.Kind == NotifyKind.HighUsage);

        // 20 min later: still in cooldown → no re-fire.
        now = now.AddMinutes(20);
        ev.UtcNow = () => now;
        var middle = ev.Evaluate(CooldownSession(92)).ToList();
        Assert.DoesNotContain(middle, e => e.Kind == NotifyKind.HighUsage);

        // 31 min after first: cooldown elapsed → re-fires.
        now = now.AddMinutes(11);   // total 31 min from first
        ev.UtcNow = () => now;
        var after = ev.Evaluate(CooldownSession(93)).ToList();
        Assert.Single(after, e => e.Kind == NotifyKind.HighUsage);
    }

    // Dropping below threshold re-arms; next crossing fires again regardless of cooldown.
    [Fact]
    public void Cooldown_ReArmsAfterDropBelowThreshold()
    {
        var now = new DateTime(2000, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var ev  = new NotificationEvaluator { UtcNow = () => now, HighUsageCooldownMinutes = 60 };

        ev.Evaluate(CooldownSession(91)).ToList(); // fires, arms cooldown

        // Drop below threshold → re-arms warnedHigh flag and clears cooldown timestamp.
        ev.Evaluate(CooldownSession(80)).ToList();

        // Next crossing fires immediately even though cooldown would have prevented it.
        var rearm = ev.Evaluate(CooldownSession(92)).ToList();
        Assert.Single(rearm, e => e.Kind == NotifyKind.HighUsage);
    }
}
