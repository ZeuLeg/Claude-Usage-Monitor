namespace ClaudeUsageMonitor;

public enum NotifyKind { HighUsage, LimitReached, Reset }

public record NotifyEvent(NotifyKind Kind, string Quota, double Percent, string ResetText);

public sealed class NotificationEvaluator
{
    private QuotaState _session = new();
    private QuotaState _weekly = new();

    public IEnumerable<NotifyEvent> Evaluate(UsageData data)
    {
        foreach (var ev in _session.Evaluate(
            data.SessionPercent, data.SessionResetsAt, data.SessionResetText, "5h session"))
        {
            yield return ev;
        }

        if (data.HasWeekly)
        {
            foreach (var ev in _weekly.Evaluate(
                data.WeeklyPercent, data.WeeklyResetsAt, data.WeeklyResetText, "7d weekly"))
            {
                yield return ev;
            }
        }
    }

    private sealed class QuotaState
    {
        private static readonly TimeSpan ResetTolerance = TimeSpan.FromMinutes(1);

        private bool _warned90;
        private bool _reached100;
        private DateTime? _lastResetsAt;

        public IEnumerable<NotifyEvent> Evaluate(
            double pct, DateTime? resetsAt, string resetText, string quotaLabel)
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
                    _warned90 = false;
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

            if (pct >= 90.0)
            {
                if (!_warned90)
                {
                    yield return new NotifyEvent(NotifyKind.HighUsage, quotaLabel, pct, resetText);
                    _warned90 = true;
                }
            }
            else
            {
                _warned90 = false;
            }
        }
    }
}
