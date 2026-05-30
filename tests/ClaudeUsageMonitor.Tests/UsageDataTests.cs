using ClaudeUsageMonitor;
using Xunit;

namespace ClaudeUsageMonitor.Tests;

public class UsageDataTests
{
    // ── FormatSpan ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0,   "--:--")]
    [InlineData(-60, "--:--")]
    public void FormatSpan_ZeroOrNegative_ReturnsDashes(int seconds, string expected)
    {
        Assert.Equal(expected, UsageData.FormatSpan(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void FormatSpan_LessThanHour_ShowsMinutes()
    {
        Assert.Equal("45m", UsageData.FormatSpan(TimeSpan.FromMinutes(45)));
    }

    [Fact]
    public void FormatSpan_MoreThanHour_ShowsHoursAndMinutes()
    {
        Assert.Equal("2h 15m", UsageData.FormatSpan(TimeSpan.FromMinutes(135)));
    }

    [Fact]
    public void FormatSpan_MoreThanDay_ShowsDaysAndHours()
    {
        Assert.Equal("1d 6h", UsageData.FormatSpan(TimeSpan.FromHours(30)));
    }

    // ── MergeCarryForward ──────────────────────────────────────────────────────

    [Fact]
    public void MergeCarryForward_PreviousHasWeekly_CurrentDoesNot_CarriesForward()
    {
        var previous = new UsageData
        {
            HasWeekly      = true,
            WeeklyPercent  = 55.0,
            WeeklyResetsAt = DateTime.UtcNow.AddDays(3),
        };
        var current = new UsageData { HasWeekly = false };

        var result = UsageData.MergeCarryForward(previous, current);

        Assert.True(result.HasWeekly);
        Assert.True(result.WeeklyStale);
        Assert.Equal(55.0, result.WeeklyPercent);
        Assert.Equal(previous.WeeklyResetsAt, result.WeeklyResetsAt);
    }

    [Fact]
    public void MergeCarryForward_CurrentHasWeekly_NotStale()
    {
        var previous = new UsageData { HasWeekly = true, WeeklyPercent = 50.0 };
        var current  = new UsageData { HasWeekly = true, WeeklyPercent = 60.0 };

        var result = UsageData.MergeCarryForward(previous, current);

        Assert.False(result.WeeklyStale);
        Assert.Equal(60.0, result.WeeklyPercent);
    }

    [Fact]
    public void MergeCarryForward_NoPrevious_ReturnsCurrentUnchanged()
    {
        var current = new UsageData { HasWeekly = false };

        var result = UsageData.MergeCarryForward(null, current);

        Assert.False(result.HasWeekly);
        Assert.False(result.WeeklyStale);
    }

    [Fact]
    public void MergeCarryForward_PreviousHadNoWeekly_DoesNotCarry()
    {
        var previous = new UsageData { HasWeekly = false };
        var current  = new UsageData { HasWeekly = false };

        var result = UsageData.MergeCarryForward(previous, current);

        Assert.False(result.HasWeekly);
        Assert.False(result.WeeklyStale);
    }
}

public class Win32InteropTests
{
    [Fact]
    public void WS_EX_TOPMOST_HasCorrectValue()
    {
        Assert.Equal(0x00000008u, Win32Interop.WS_EX_TOPMOST);
    }
}

public class PollPolicyTests
{
    [Fact]
    public void NextBackoff_Doubles_ThenCaps()
    {
        Assert.Equal(240_000, ClaudeUsageMonitor.UsagePoller.NextBackoff(120_000)); // doubles under cap
        Assert.Equal(300_000, ClaudeUsageMonitor.UsagePoller.NextBackoff(240_000)); // 480k → capped at 300k
    }

    [Fact]
    public void NextBackoff_CapsAtFiveMinutes()
    {
        Assert.Equal(300_000, ClaudeUsageMonitor.UsagePoller.NextBackoff(300_000));
        Assert.Equal(300_000, ClaudeUsageMonitor.UsagePoller.NextBackoff(10_000_000));
    }

    [Fact]
    public void ShouldShowStaleIcon_TrueOnlyWhenWeHavePreviousData()
    {
        Assert.False(ClaudeUsageMonitor.UsagePoller.ShouldShowStaleIcon(null));
        Assert.True(ClaudeUsageMonitor.UsagePoller.ShouldShowStaleIcon(new UsageData()));
    }
}
