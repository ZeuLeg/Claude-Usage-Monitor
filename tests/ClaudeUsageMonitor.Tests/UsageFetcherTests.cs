using ClaudeUsageMonitor;
using Xunit;

namespace ClaudeUsageMonitor.Tests;

public class UsageFetcherTests
{
    [Fact]
    public void Parse_FullResponse_ParsesAllFields()
    {
        var json = """
            {
              "five_hour":  { "utilization": 42.5, "resets_at": "2025-02-17T18:00:00Z" },
              "seven_day":  { "utilization": 13.0, "resets_at": "2025-02-19T07:00:00Z" },
              "extra_usage": { "is_enabled": true, "monthly_limit": 5000, "used_credits": 1250, "utilization": 25.0 }
            }
            """;

        var data = UsageFetcher.Parse(json);

        Assert.Equal(42.5, data.SessionPercent);
        Assert.NotNull(data.SessionResetsAt);
        Assert.Equal(13.0, data.WeeklyPercent);
        Assert.True(data.HasWeekly);
        Assert.NotNull(data.WeeklyResetsAt);
        Assert.True(data.ExtraEnabled);
        Assert.Equal(12.50m, data.ExtraUsedDollars);
        Assert.Equal(50.00m, data.ExtraLimitDollars);
    }

    [Fact]
    public void Parse_NoSevenDay_HasWeeklyFalse()
    {
        var json = """{ "five_hour": { "utilization": 30.0, "resets_at": "2025-02-17T18:00:00Z" } }""";

        var data = UsageFetcher.Parse(json);

        Assert.False(data.HasWeekly);
        Assert.Equal(30.0, data.SessionPercent);
    }

    [Fact]
    public void Parse_NoFiveHour_SessionZero()
    {
        var json = """{ "seven_day": { "utilization": 20.0 } }""";

        var data = UsageFetcher.Parse(json);

        Assert.Equal(0.0, data.SessionPercent);
        Assert.True(data.HasWeekly);
    }

    [Fact]
    public void Parse_ExtraUsage_ConvertsCentsToDollars()
    {
        var json = """
            { "extra_usage": { "is_enabled": true, "monthly_limit": 10000, "used_credits": 2500, "utilization": 25.0 } }
            """;

        var data = UsageFetcher.Parse(json);

        Assert.Equal(25.00m, data.ExtraUsedDollars);
        Assert.Equal(100.00m, data.ExtraLimitDollars);
    }

    [Fact]
    public void Parse_InvalidJson_ThrowsException()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => UsageFetcher.Parse("not json"));
    }

    [Fact]
    public void Parse_EmptyObject_ReturnsDefaults()
    {
        var data = UsageFetcher.Parse("{}");

        Assert.Equal(0.0, data.SessionPercent);
        Assert.False(data.HasWeekly);
        Assert.False(data.ExtraEnabled);
    }

    [Fact]
    public void Parse_WithOpusBlock_ParsesOpusPercent()
    {
        var json = """
            {
              "five_hour":     { "utilization": 42.5, "resets_at": "2025-02-17T18:00:00Z" },
              "seven_day":     { "utilization": 13.0, "resets_at": "2025-02-19T07:00:00Z" },
              "seven_day_opus":{ "utilization": 18.0, "resets_at": "2025-02-19T07:00:00Z" }
            }
            """;

        var data = UsageFetcher.Parse(json);

        Assert.True(data.HasOpus);
        Assert.Equal(18.0, data.OpusPercent!.Value, precision: 4);
        Assert.NotNull(data.OpusResetsAt);
    }

    [Fact]
    public void Parse_WithoutOpusBlock_HasOpusFalse()
    {
        var json = """{ "five_hour": { "utilization": 30.0, "resets_at": "2025-02-17T18:00:00Z" } }""";

        var data = UsageFetcher.Parse(json);

        Assert.False(data.HasOpus);
        Assert.Null(data.OpusPercent);
    }
}
