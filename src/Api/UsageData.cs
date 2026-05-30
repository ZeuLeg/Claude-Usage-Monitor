namespace ClaudeUsageMonitor;

public enum PaceState { Under, OnPace, Ahead }

/// <summary>
/// Data model for GET https://api.anthropic.com/api/oauth/usage
///
/// Response:
/// {
///   "five_hour":  { "utilization": 42.5, "resets_at": "2025-02-17T18:00:00Z" },
///   "seven_day":  { "utilization": 13.0, "resets_at": "2025-02-19T07:00:00Z" },
///   "extra_usage": { "is_enabled": true, "monthly_limit": 5000, "used_credits": 1250, "utilization": 25.0 }
/// }
/// </summary>
public sealed class UsageData
{
    public double SessionPercent { get; set; }
    public DateTime? SessionResetsAt { get; set; }

    public double WeeklyPercent { get; set; }
    public DateTime? WeeklyResetsAt { get; set; }
    public bool HasWeekly { get; set; }

    // Extra Usage (Pay-as-you-go Overage)
    public bool ExtraEnabled { get; set; }
    public double ExtraPercent { get; set; }
    public decimal ExtraUsedDollars { get; set; }
    public decimal ExtraLimitDollars { get; set; }

    public DateTime FetchedAt  { get; set; } = DateTime.Now;
    // True when weekly data was carried forward from a previous poll (API returned no seven_day).
    public bool WeeklyStale    { get; set; }

    // --- Computed Properties ---

    public TimeSpan SessionResetIn => TimeUntil(SessionResetsAt);
    public TimeSpan WeeklyResetIn => TimeUntil(WeeklyResetsAt);

    public string SessionResetText => FormatSpan(SessionResetIn);
    public string WeeklyResetText => FormatSpan(WeeklyResetIn);

    // Pacing: linear expected usage based on elapsed time in the window
    private static readonly TimeSpan SessionWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeeklyWindow = TimeSpan.FromDays(7);

    public double SessionExpectedPercent => ExpectedPercent(SessionResetsAt, SessionWindow);
    public double WeeklyExpectedPercent => ExpectedPercent(WeeklyResetsAt, WeeklyWindow);

    public double SessionPaceDiff => SessionPercent - SessionExpectedPercent;
    public double WeeklyPaceDiff => WeeklyPercent - WeeklyExpectedPercent;

    public PaceState SessionPaceState => ClassifyPace(SessionPaceDiff);
    public PaceState WeeklyPaceState  => ClassifyPace(WeeklyPaceDiff);

    public string SessionPaceText => FormatPace(SessionPaceDiff);
    public string WeeklyPaceText => FormatPace(WeeklyPaceDiff);

    public string TooltipText
    {
        get
        {
            var s = $"Session: {SessionPercent:0}% | Reset: {SessionResetText}";
            if (HasWeekly)
            {
                s += $"\nWeekly: {WeeklyPercent:0}% ({WeeklyPaceText})";
            }
            if (ExtraEnabled) s += $"\nExtra: ${ExtraUsedDollars:F2}/${ExtraLimitDollars:F2}";
            s += $"\nUpdated: {FetchedAt:HH:mm:ss}";
            return s.Length > 127 ? s[..127] : s;
        }
    }

    private static TimeSpan TimeUntil(DateTime? utc)
    {
        if (!utc.HasValue) return TimeSpan.Zero;
        var d = utc.Value.ToLocalTime() - DateTime.Now;
        return d > TimeSpan.Zero ? d : TimeSpan.Zero;
    }

    private static PaceState ClassifyPace(double diff) =>
        diff >= 5 ? PaceState.Ahead : diff <= -5 ? PaceState.Under : PaceState.OnPace;

    internal static string FormatSpan(TimeSpan ts)
    {
        if (ts <= TimeSpan.Zero) return "--:--";
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }

    private static double ExpectedPercent(DateTime? resetsAtUtc, TimeSpan window)
    {
        if (!resetsAtUtc.HasValue) return 0;
        var windowStart = resetsAtUtc.Value.ToLocalTime() - window;
        var elapsed = DateTime.Now - windowStart;
        if (elapsed <= TimeSpan.Zero) return 0;
        var pct = elapsed.TotalSeconds / window.TotalSeconds * 100;
        return Math.Clamp(pct, 0, 100);
    }

    /// <summary>
    /// When the API omits seven_day (can happen around reset time), carry the last
    /// known weekly values forward so the widget doesn't flash as if the week reset.
    /// </summary>
    public static UsageData MergeCarryForward(UsageData? previous, UsageData current)
    {
        if (current.HasWeekly || previous == null || !previous.HasWeekly)
            return current;

        current.WeeklyPercent  = previous.WeeklyPercent;
        current.WeeklyResetsAt = previous.WeeklyResetsAt;
        current.HasWeekly      = true;
        current.WeeklyStale    = true;
        return current;
    }

    private static string FormatPace(double diff)
    {
        if (diff >= 5) return $"+{diff:0}% ahead";
        if (diff <= -5) return $"{diff:0}% under";
        return "on pace";
    }
}
