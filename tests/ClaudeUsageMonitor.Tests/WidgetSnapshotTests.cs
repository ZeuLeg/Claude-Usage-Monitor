using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Xunit;

namespace ClaudeUsageMonitor.Tests;

/// <summary>
/// Renders each widget scenario to a PNG in _snapshots/.
/// Assertions: no exception thrown + file written. No pixel comparison.
/// </summary>
public class WidgetSnapshotTests
{
    private static readonly string SnapshotDir = Path.Combine(
        Path.GetDirectoryName(typeof(WidgetSnapshotTests).Assembly.Location)!,
        "_snapshots");

    private const int W = 290;
    private const int H = 46;

    private static void Snapshot(string name, bool light, UsageData? data, BurnRateTracker? burnRate = null)
    {
        Directory.CreateDirectory(SnapshotDir);
        string suffix = light ? "_light" : "_dark";
        string path   = Path.Combine(SnapshotDir, $"{name}{suffix}.png");

        using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            TaskbarWidget.Render(g, W, H, light, data, burnRate);

        bmp.Save(path, ImageFormat.Png);
        Assert.True(File.Exists(path), $"Snapshot not written: {path}");
    }

    private static void Both(string name, UsageData? data, BurnRateTracker? burnRate = null)
    {
        Snapshot(name, light: true,  data, burnRate);
        Snapshot(name, light: false, data, burnRate);
    }

    /// <summary>
    /// Saves a tray icon bitmap scaled up 4x (64x64 → 256x256 nearest-neighbor-free,
    /// rendered natively at 64px) for easy visual inspection.
    /// </summary>
    private static void TraySnapshot(string name, Bitmap bmp)
    {
        Directory.CreateDirectory(SnapshotDir);
        string path = Path.Combine(SnapshotDir, $"tray_{name}.png");

        // The source bitmap is already 64x64; save at 4x = 256x256 using high-quality bicubic
        // so circles look smooth rather than pixelated.
        using var scaled = new Bitmap(256, 256);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, 0, 0, 256, 256);
        }

        scaled.Save(path, ImageFormat.Png);
        Assert.True(File.Exists(path), $"Tray snapshot not written: {path}");
    }

    // ── Widget Scenarios ─────────────────────────────────────────────────────

    [Fact]
    public void Normal()
    {
        // session 42%, reset in 2h 10m; weekly 18%, reset in 3d 4h
        var data = new UsageData
        {
            SessionPercent  = 42,
            SessionResetsAt = DateTime.UtcNow.AddHours(2).AddMinutes(10),
            WeeklyPercent   = 18,
            WeeklyResetsAt  = DateTime.UtcNow.AddDays(3).AddHours(4),
            HasWeekly       = true,
        };
        Both("normal", data);
    }

    [Fact]
    public void HighEta()
    {
        // session 92%, reset in ~4h 59m; burn rate 20%/h → ETA ~24m < resetIn → ETA shown
        var now = DateTime.UtcNow;
        var data = new UsageData
        {
            SessionPercent  = 92,
            SessionResetsAt = now.AddHours(4).AddMinutes(59),
            WeeklyPercent   = 55,
            WeeklyResetsAt  = now.AddDays(2),
            HasWeekly       = true,
        };

        // Two samples ≥10 min apart: +10% in 30 min → 20%/h burn → ETA ≈ 24 min
        var burnRate = new BurnRateTracker();
        burnRate.AddSample(now.AddMinutes(-30), 82.0);
        burnRate.AddSample(now,                 92.0);

        Both("high_eta", data, burnRate);
    }

    [Fact]
    public void Longest()
    {
        // Widest possible text:
        //   session FormatSpan → "4h 59m"  (just under 5 hours)
        //   weekly  FormatSpan → "6d 23h"  (just under 7 days)
        var data = new UsageData
        {
            SessionPercent  = 65,
            SessionResetsAt = DateTime.UtcNow.AddHours(4).AddMinutes(59),
            WeeklyPercent   = 40,
            WeeklyResetsAt  = DateTime.UtcNow.AddDays(6).AddHours(23),
            HasWeekly       = true,
        };
        Both("longest", data);
    }

    [Fact]
    public void Opus()
    {
        // weekly + reset time + OpusPercent — Row2 must show "3d 3h · Op 7"
        var data = new UsageData
        {
            SessionPercent  = 30,
            SessionResetsAt = DateTime.UtcNow.AddHours(3),
            WeeklyPercent   = 18,
            WeeklyResetsAt  = DateTime.UtcNow.AddDays(3).AddHours(3),
            HasWeekly       = true,
            OpusPercent     = 7,
        };
        Both("opus", data);
    }

    [Fact]
    public void Loading()
    {
        // data == null → loading state
        Both("loading", data: null);
    }

    [Fact]
    public void NoWeekly()
    {
        // HasWeekly false → second row shows dashes
        var data = new UsageData
        {
            SessionPercent  = 55,
            SessionResetsAt = DateTime.UtcNow.AddHours(1).AddMinutes(30),
            HasWeekly       = false,
        };
        Both("noweekly", data);
    }

    [Fact]
    public void WeeklyHours()
    {
        // Widest time string for weekly row: "23h 59m" — verifies "m" is never clipped.
        // Session also near maximum: "4h 59m".
        var data = new UsageData
        {
            SessionPercent  = 50,
            SessionResetsAt = DateTime.UtcNow.AddHours(4).AddMinutes(59),
            WeeklyPercent   = 30,
            WeeklyResetsAt  = DateTime.UtcNow.AddHours(23).AddMinutes(59),
            HasWeekly       = true,
        };
        Both("weekly_hours", data);
    }

    // ── HoverCard Scenarios ──────────────────────────────────────────────────

    private static void HoverSnapshot(string name, bool light, UsageData data, BurnRateTracker? burnRate = null)
    {
        Directory.CreateDirectory(SnapshotDir);
        string suffix = light ? "_light" : "_dark";
        string path   = Path.Combine(SnapshotDir, $"{name}{suffix}.png");

        using var bmp = new Bitmap(HoverCard.CardW, HoverCard.CardH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            HoverCard.Render(g, HoverCard.CardW, HoverCard.CardH, light, data, burnRate);

        bmp.Save(path, ImageFormat.Png);
        Assert.True(File.Exists(path), $"Snapshot not written: {path}");
    }

    [Fact]
    public void HoverCardLight()
    {
        var now  = DateTime.UtcNow;
        var data = new UsageData
        {
            PlanLabel       = "Max 20x",
            SessionPercent  = 86,
            SessionResetsAt = now.AddHours(2).AddMinutes(15),
            WeeklyPercent   = 34,
            WeeklyResetsAt  = now.AddDays(4).AddHours(10),
            HasWeekly       = true,
            OpusPercent     = 7,
        };

        // Burn rate: +20%/h → ETA ≈ ~42m which is < resetIn of 2h15m → ETA row shown
        var burnRate = new BurnRateTracker();
        burnRate.AddSample(now.AddMinutes(-30), 76.0);
        burnRate.AddSample(now,                 86.0);

        HoverSnapshot("hovercard", light: true, data, burnRate);
    }

    [Fact]
    public void HoverCardDark()
    {
        var now  = DateTime.UtcNow;
        var data = new UsageData
        {
            PlanLabel       = "Max 20x",
            SessionPercent  = 86,
            SessionResetsAt = now.AddHours(2).AddMinutes(15),
            WeeklyPercent   = 34,
            WeeklyResetsAt  = now.AddDays(4).AddHours(10),
            HasWeekly       = true,
            OpusPercent     = 7,
        };

        var burnRate = new BurnRateTracker();
        burnRate.AddSample(now.AddMinutes(-30), 76.0);
        burnRate.AddSample(now,                 86.0);

        HoverSnapshot("hovercard", light: false, data, burnRate);
    }

    // ── Tray Icon Scenarios ──────────────────────────────────────────────────

    [Fact]
    public void TrayNormal()
    {
        // normal ~42% green session
        var data = new UsageData
        {
            SessionPercent = 42,
            SessionResetsAt = DateTime.UtcNow.AddHours(2),
            WeeklyPercent  = 18,
            WeeklyResetsAt = DateTime.UtcNow.AddDays(3),
            HasWeekly      = true,
        };
        using var bmp = TrayIconRenderer.RenderUsageIcon(data);
        TraySnapshot("normal_42pct_green", bmp);
    }

    [Fact]
    public void TrayCritical()
    {
        // 92% red session
        var data = new UsageData
        {
            SessionPercent = 92,
            SessionResetsAt = DateTime.UtcNow.AddHours(1),
            WeeklyPercent  = 60,
            WeeklyResetsAt = DateTime.UtcNow.AddDays(2),
            HasWeekly      = true,
        };
        using var bmp = TrayIconRenderer.RenderUsageIcon(data);
        TraySnapshot("critical_92pct_red", bmp);
    }

    [Fact]
    public void TraySignedOut()
    {
        // signed-out: grey text icon "–"
        using var bmp = TrayIconRenderer.RenderTextIcon("–", Palette.Gray, 64);
        TraySnapshot("signed_out_gray", bmp);
    }

    [Fact]
    public void TrayError()
    {
        // ERR state: text icon
        using var bmp = TrayIconRenderer.RenderTextIcon("ERR", Palette.Crit, 64);
        TraySnapshot("error_red", bmp);
    }
}
