using System.Drawing;

namespace ClaudeUsageMonitor;

/// <summary>Shared status colors used by the tray icon, widget and popup.</summary>
internal static class Palette
{
    public static readonly Color Ok     = Color.FromArgb(34, 197, 94);
    public static readonly Color Warn   = Color.FromArgb(251, 191, 36);
    public static readonly Color Crit   = Color.FromArgb(239, 68, 68);
    public static readonly Color Gray   = Color.FromArgb(156, 163, 175);
    public static readonly Color Weekly = Color.FromArgb(56, 189, 248); // cyan — weekly reference

    // over-pace = red (burning quota fast), under-pace = green (headroom), on-pace = yellow
    public static Color Pace(double diff) => diff >= 5 ? Crit : diff <= -5 ? Ok : Warn;
}
