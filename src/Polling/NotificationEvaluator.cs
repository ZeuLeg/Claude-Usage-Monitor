namespace ClaudeUsageMonitor;

public enum NotifyKind { HighUsage, LimitReached, Reset, DepletionSoon }

public record NotifyEvent(NotifyKind Kind, string Quota, double Percent, string ResetText, TimeSpan? EtaToFull = null);

public sealed class NotificationEvaluator
{
    /// <summary>Percent at which a HighUsage event fires. Configurable; default 90.</summary>
    public double HighUsageThreshold { get; set; } = 90.0;

    /// <summary>
    /// Minutes between repeated HighUsage notifications while usage stays above the threshold.
    /// 0 = fire only once per window (original behavior).
    /// </summary>
    public int HighUsageCooldownMinutes { get; set; } = 0;

    /// <summary>
    /// Clock source — injectable for deterministic tests. Defaults to UTC wall clock.
    /// Both the local-timer reset path and the cooldown timer use this.
    /// </summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private QuotaState _session = new();
    private QuotaState _weekly = new();

    public IEnumerable<NotifyEvent> Evaluate(UsageData data, TimeSpan? sessionEtaToFull = null)
    {
        foreach (var ev in _session.Evaluate(
            data.SessionPercent, data.SessionResetsAt, data.SessionResetText, "5h session",
            HighUsageThreshold, HighUsageCooldownMinutes, UtcNow, sessionEtaToFull))
        {
            yield return ev;
        }

        if (data.HasWeekly)
        {
            foreach (var ev in _weekly.Evaluate(
                data.WeeklyPercent, data.WeeklyResetsAt, data.WeeklyResetText, "7d weekly",
                HighUsageThreshold, HighUsageCooldownMinutes, UtcNow))
            {
                yield return ev;
            }
        }
    }

    private sealed class QuotaState
    {
        private static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(1);

        private bool _warnedHigh;
        private bool _reached100;
        private bool _warnedDepletion;
        private DateTime? _lastResetsAt;
        // Tracks the resetsAt window for which a local-timer Reset was already fired,
        // so API-driven detection doesn't double-fire for the same window.
        private DateTime? _localResetFiredFor;
        // When the last HighUsage cooldown notification was sent (null = not yet sent in this arm).
        private DateTime? _lastHighUsageAt;

        public IEnumerable<NotifyEvent> Evaluate(
            double pct, DateTime? resetsAt, string resetText, string quotaLabel,
            double highUsageThreshold, int cooldownMinutes, Func<DateTime> utcNow,
            TimeSpan? etaToFull = null)
        {
            var now = utcNow();
            bool didReset = false;

            // ── Local-timer reset: fire when the known resetsAt moment has passed ──
            // This fires before the next regular poll arrives, giving timely notification.
            // We also advance _lastResetsAt to resetsAt so the subsequent API-driven
            // path (which compares against _lastResetsAt) sees the correct baseline
            // and does not produce a duplicate Reset for the same elapsed window.
            if (_lastResetsAt != null
                && resetsAt.HasValue && resetsAt.Value != DateTime.MinValue
                && now >= resetsAt.Value
                && _localResetFiredFor != resetsAt.Value)
            {
                _localResetFiredFor = resetsAt.Value;
                _lastResetsAt = resetsAt.Value;  // advance baseline so API-driven path skips this window
                _warnedHigh = false;
                _reached100 = false;
                _warnedDepletion = false;
                _lastHighUsageAt = null;
                didReset = true;
                yield return new NotifyEvent(NotifyKind.Reset, quotaLabel, pct, resetText);
            }

            // ── API-driven reset: new resetsAt window arrived from API ──
            if (_lastResetsAt == null)
            {
                _lastResetsAt = resetsAt ?? DateTime.MinValue;
            }
            else if (resetsAt.HasValue)
            {
                var delta = resetsAt.Value - _lastResetsAt.Value;
                if (delta > ResetTolerance)
                {
                    yield return new NotifyEvent(NotifyKind.Reset, quotaLabel, pct, resetText);
                    _warnedHigh = false;
                    _reached100 = false;
                    _warnedDepletion = false;
                    _lastHighUsageAt = null;
                    didReset = true;
                }
                if (delta > TimeSpan.Zero)
                    _lastResetsAt = resetsAt;   // never lower the baseline on jitter
            }

            if (pct >= 99.5)
            {
                if (!_reached100)
                {
                    yield return new NotifyEvent(NotifyKind.LimitReached, quotaLabel, pct, resetText);
                    _reached100 = true;
                }
            }
            else
            {
                _reached100 = false;
            }

            if (pct >= highUsageThreshold)
            {
                if (!_warnedHigh)
                {
                    // First crossing: fire immediately and arm the cooldown clock.
                    yield return new NotifyEvent(NotifyKind.HighUsage, quotaLabel, pct, resetText);
                    _warnedHigh = true;
                    _lastHighUsageAt = now;
                }
                else if (cooldownMinutes > 0 && _lastHighUsageAt.HasValue)
                {
                    // Already warned: repeat only after cooldown has elapsed.
                    var elapsed = now - _lastHighUsageAt.Value;
                    if (elapsed >= TimeSpan.FromMinutes(cooldownMinutes))
                    {
                        yield return new NotifyEvent(NotifyKind.HighUsage, quotaLabel, pct, resetText);
                        _lastHighUsageAt = now;
                    }
                }
            }
            else
            {
                _warnedHigh = false;
                _lastHighUsageAt = null;
            }

            // ── DepletionSoon: ETA to full < remaining session time, pct >= 50 ──
            // Skip if a reset fired this same turn — the flag was just cleared and the
            // current usage snapshot reflects the old window, not the new one.
            if (!didReset && etaToFull.HasValue && resetsAt.HasValue && pct >= 50.0 && !_warnedDepletion)
            {
                var remaining = resetsAt.Value - now;
                if (etaToFull.Value < remaining)
                {
                    _warnedDepletion = true;
                    yield return new NotifyEvent(NotifyKind.DepletionSoon, quotaLabel, pct, resetText, etaToFull);
                }
            }
        }
    }
}
