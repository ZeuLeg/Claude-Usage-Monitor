namespace ClaudeUsageMonitor;

/// <summary>
/// Tracks session usage percent samples over a 60-minute sliding window
/// and derives a burn rate (% per hour) and an ETA to full (100%).
/// </summary>
internal sealed class BurnRateTracker
{
    private static readonly TimeSpan WindowDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinSpan        = TimeSpan.FromMinutes(10);
    private const double ResetDropThreshold         = 10.0; // pct drop that signals a reset

    private readonly List<(DateTime Utc, double Pct)> _samples = new();
    private readonly object _gate = new();

    /// <summary>Adds a new usage sample. Drops stale samples outside the 60-min window.</summary>
    public void AddSample(DateTime utc, double pct)
    {
        lock (_gate)
        {
            // Significant drop → usage window reset; clear all prior samples so the
            // rate calculation doesn't span across the reset boundary.
            if (_samples.Count > 0 && pct < _samples[^1].Pct - ResetDropThreshold)
                _samples.Clear();

            _samples.Add((utc, pct));
            PruneOld(utc);
        }
    }

    /// <summary>
    /// Burn rate in percent per hour, or null when fewer than 2 samples or
    /// the oldest-to-newest span is less than 10 minutes.
    /// </summary>
    public double? RatePerHour
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count < 2) return null;
                var oldest = _samples[0];
                var newest = _samples[^1];
                var span   = newest.Utc - oldest.Utc;
                if (span < MinSpan) return null;
                var rise = newest.Pct - oldest.Pct;
                return rise / span.TotalHours;
            }
        }
    }

    /// <summary>
    /// Estimated time until 100% is reached at the current burn rate,
    /// or null when rate is unavailable or non-positive.
    /// </summary>
    public TimeSpan? EstimateToFull(double currentPct)
    {
        var rate = RatePerHour;
        if (rate == null || rate.Value <= 0) return null;
        var hoursLeft = (100.0 - currentPct) / rate.Value;
        if (hoursLeft <= 0) return TimeSpan.Zero;
        return TimeSpan.FromHours(hoursLeft);
    }

    private void PruneOld(DateTime referenceUtc)
    {
        var cutoff = referenceUtc - WindowDuration;
        // Keep at least the oldest sample so the span always covers ≥ window boundary
        int removeCount = 0;
        for (int i = 0; i < _samples.Count - 1; i++)
        {
            if (_samples[i].Utc < cutoff) removeCount++;
            else break;
        }
        if (removeCount > 0) _samples.RemoveRange(0, removeCount);
    }
}
