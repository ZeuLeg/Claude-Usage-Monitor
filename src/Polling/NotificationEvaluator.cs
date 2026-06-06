namespace ClaudeUsageMonitor;

public enum NotifyKind { HighUsage, LimitReached, Reset }

public record NotifyEvent(NotifyKind Kind, string Quota, double Percent, string ResetText);

public sealed class NotificationEvaluator
{
    /// <summary>Percent at which a HighUsage event fires. Configurable; default 90.</summary>
    public double HighUsageThreshold { get; set; } = 90.0;

    private QuotaState _session = new();
    private QuotaState _weekly = new();

    public IEnumerable<NotifyEvent> Evaluate(UsageData data)
    {
        foreach (var ev in _session.Evaluate(
            data.SessionPercent, data.SessionResetsAt, data.SessionResetText, "5h session", HighUsageThreshold))
        {
            yield return ev;
        }

        if (data.HasWeekly)
        {
            foreach (var ev in _weekly.Evaluate(
                data.WeeklyPercent, data.WeeklyResetsAt, data.WeeklyResetText, "7d weekly", HighUsageThreshold))
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
        private DateTime? _lastResetsAt;

        public IEnumerable<NotifyEvent> Evaluate(
            double pct, DateTime? resetsAt, string resetText, string quotaLabel, double highUsageThreshold)
        {
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
                    yield return new NotifyEvent(NotifyKind.HighUsage, quotaLabel, pct, resetText);
                    _warnedHigh = true;
                }
            }
            else
            {
                _warnedHigh = false;
            }
        }
    }
}
