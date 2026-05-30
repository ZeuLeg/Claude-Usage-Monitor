using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeUsageMonitor;

/// <summary>
/// Owns the tray <see cref="NotifyIcon"/>'s image + native HICON handle and renders it:
/// the dual progress-ring usage icon, a dimmed "stale" variant, or a text fallback for
/// error states ("!", "AUTH", "ERR"). All swaps are marshalled onto the UI thread.
/// </summary>
internal sealed class TrayIconRenderer : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly Control _invoker;
    private IntPtr _handle = IntPtr.Zero;

    public TrayIconRenderer(NotifyIcon notify, Control invoker)
    {
        _notify  = notify;
        _invoker = invoker;
    }

    public void ShowText(string text, Color color, string tooltip)
    {
        if (_invoker.InvokeRequired) { _invoker.BeginInvoke(() => ShowText(text, color, tooltip)); return; }
        Swap(MakeIcon(text, color), tooltip);
    }

    public void ShowUsage(UsageData data)
    {
        if (_invoker.InvokeRequired) { _invoker.BeginInvoke(() => ShowUsage(data)); return; }
        Swap(MakeIconVisual(data), data.TooltipText);
    }

    public void ShowStale(UsageData data, string reason)
    {
        if (_invoker.InvokeRequired) { _invoker.BeginInvoke(() => ShowStale(data, reason)); return; }
        Swap(MakeIconVisual(data, dim: true), $"{reason}\nLast updated: {data.FetchedAt:HH:mm}");
    }

    private void Swap((Icon icon, IntPtr hicon) made, string tooltip)
    {
        var old       = _notify.Icon;
        var oldHandle = _handle;
        _notify.Icon  = made.icon;
        _handle       = made.hicon;
        _notify.Text  = Truncate(tooltip);
        old?.Dispose();
        if (oldHandle != IntPtr.Zero) Win32Interop.DestroyIcon(oldHandle);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) { Win32Interop.DestroyIcon(_handle); _handle = IntPtr.Zero; }
    }

    private static string Truncate(string text)
    {
        if (text.Length <= 127) return text;
        var cut = text.LastIndexOf('\n', 126);
        return cut > 0 ? text[..cut] : text[..127];
    }

    private static (Icon icon, IntPtr hicon) MakeIcon(string text, Color color)
    {
        const int sz = 32;
        using var bmp = new Bitmap(sz, sz);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        using var bg = new SolidBrush(Color.FromArgb(30, 30, 30));
        var r = new Rectangle(0, 0, sz, sz);
        using var rr = new GraphicsPath();
        rr.AddArc(r.X, r.Y, 8, 8, 180, 90);
        rr.AddArc(r.Right - 8, r.Y, 8, 8, 270, 90);
        rr.AddArc(r.Right - 8, r.Bottom - 8, 8, 8, 0, 90);
        rr.AddArc(r.X, r.Bottom - 8, 8, 8, 90, 90);
        rr.CloseFigure();
        g.FillPath(bg, rr);

        using var font = new Font("Segoe UI", text.Length > 3 ? 7f : 9f, FontStyle.Bold);
        using var brush = new SolidBrush(color);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, new RectangleF(0, 0, sz, sz), fmt);

        var hicon = bmp.GetHicon();
        return (Icon.FromHandle(hicon), hicon);
    }

    private static (Icon icon, IntPtr hicon) MakeIconVisual(UsageData data, bool dim = false)
    {
        // Rendered at 64px (downscaled by the tray) for crisper, bolder rings.
        const int sz = 64;
        int sessA = dim ? 90 : 240;   // session arc alpha
        int weekA = dim ? 80 : 220;   // weekly arc alpha
        const float outerInset = 6f,  outerPen = 7f;
        const float innerInset = 19f, innerPen = 5.5f;
        float outerD = sz - 2 * outerInset;
        float innerD = sz - 2 * innerInset;

        using var bmp = new Bitmap(sz, sz);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using (var bg = new SolidBrush(Color.FromArgb(225, 22, 22, 30)))
            g.FillEllipse(bg, 1, 1, sz - 2, sz - 2);

        // Dim track rings
        using (var tp = new Pen(Color.FromArgb(55, 200, 200, 200), outerPen))
            g.DrawEllipse(tp, outerInset, outerInset, outerD, outerD);
        if (data.HasWeekly)
            using (var twp = new Pen(Color.FromArgb(45, 200, 200, 200), innerPen))
                g.DrawEllipse(twp, innerInset, innerInset, innerD, innerD);

        // Session arc (outer)
        if (data.SessionPercent > 0)
        {
            var sc = data.SessionPercent >= 90 ? Palette.Crit : data.SessionPercent >= 75 ? Palette.Warn : Palette.Ok;
            using var sp = new Pen(Color.FromArgb(sessA, sc), outerPen)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(sp, outerInset, outerInset, outerD, outerD,
                      -90f, (float)(Math.Min(data.SessionPercent, 100) / 100.0 * 360.0));
        }

        // Weekly arc (inner) — cyan "reference" color by default (matches the popup's
        // weekly markers), but escalates to amber/red once weekly usage gets critical.
        if (data.HasWeekly && data.WeeklyPercent > 0)
        {
            var wc = data.WeeklyPercent >= 90 ? Palette.Crit : data.WeeklyPercent >= 75 ? Palette.Warn : Palette.Weekly;
            using var wp = new Pen(Color.FromArgb(weekA, wc), innerPen)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(wp, innerInset, innerInset, innerD, innerD,
                      -90f, (float)(Math.Min(data.WeeklyPercent, 100) / 100.0 * 360.0));
        }

        if (dim)
            using (var dot = new SolidBrush(Color.FromArgb(230, 130, 130, 140)))
                g.FillEllipse(dot, sz - 18, sz - 18, 13, 13);

        var hicon = bmp.GetHicon();
        return (Icon.FromHandle(hicon), hicon);
    }
}
