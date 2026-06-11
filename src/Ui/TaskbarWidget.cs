using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;

namespace ClaudeUsageMonitor;

/// <summary>
/// A floating topmost overlay widget positioned just above the system tray.
/// Uses UpdateLayeredWindow for per-pixel alpha rendering with hover-fade.
/// </summary>
internal sealed class TaskbarWidget : IDisposable
{
    // ── Layout constants ────────────────────────────────────────────────────
    private const int WidgetW     = 290;
    private const int WidgetH     = 46;
    private const int AccentW     = 3;   // left-edge colored status strip
    private const int PadL        = 8;   // was 6
    private const int LabelW      = 20;
    private const int LabelBarGap = 4;
    private const int BarW        = 136; // was 152; extra width prevents text clipping
    private const int BarH        = 13;
    private const int BarTextGap  = 5;
    private const int PadR        = 4;
    // TextW = 290 − 8 − 20 − 4 − 136 − 5 − 4 = 113
    private const int TextW       = WidgetW - PadL - LabelW - LabelBarGap - BarW - BarTextGap - PadR;
    private const int Row1Y       = 6;
    private const int Row2Y       = 27;
    private const int BarRadius   = 2;

    // ── Colors ──────────────────────────────────────────────────────────────
    private static readonly Color FillOk   = Color.FromArgb(34,  197,  94); // green  (matches popup)
    private static readonly Color FillWarn = Color.FromArgb(251, 191,  36); // yellow (matches popup)
    private static readonly Color FillCrit = Color.FromArgb(239,  68,  68); // red    (matches popup)

    private static Color BgColor(bool light)    => light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x1C, 0x1C, 0x1C);
    private static Color TrackColor(bool light) => light ? Color.FromArgb(0x90, 0x90, 0x90) : Color.FromArgb(0x44, 0x44, 0x44);
    private static Color TextColor(bool light)  => light ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0x88, 0x88, 0x88);
    private static Color FillColor(double pct)  => pct >= 90 ? FillCrit : pct >= 75 ? FillWarn : FillOk;

    // ── State ────────────────────────────────────────────────────────────────
    private readonly WidgetNativeWindow _nw;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _topmostTimer;
    private UsageData? _data;
    private BurnRateTracker? _burnRate;

    // ── Constructor ──────────────────────────────────────────────────────────

    public TaskbarWidget(UsageData? initialData = null)
    {
        _nw = new WidgetNativeWindow(Redraw);

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) => Redraw();

        // Re-assert TOPMOST every ~2 s; Win11 can silently demote layered windows
        // when other applications are activated.
        _topmostTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _topmostTimer.Tick += (_, _) => _nw.AssertTopMost();
        _topmostTimer.Start();

        if (initialData != null)
            Update(initialData);
        else
            Redraw(); // show loading state
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public void Update(UsageData data, BurnRateTracker? burnRate = null)
    {
        _data = data;
        if (burnRate != null) _burnRate = burnRate;
        Redraw();
    }

    /// <summary>
    /// Called when the TaskbarCreated message is received (Explorer restarted).
    /// </summary>
    public void Reattach()
    {
        _nw.Reposition();
        _nw.AssertTopMost();
        Redraw();
    }

    /// <summary>
    /// Called on resume from standby. Re-runs position math so the widget
    /// doesn't drift after the taskbar relays out.
    /// </summary>
    public void Reposition()
    {
        _nw.Reposition();
        _nw.AssertTopMost();
        Redraw();
    }

    /// <summary>Hides the widget window (signed-out state).</summary>
    public void HideWidget()
    {
        if (_nw.Handle != IntPtr.Zero)
        {
            _timer.Stop();
            _topmostTimer.Stop();
            Win32Interop.ShowWindow(_nw.Handle, Win32Interop.SW_HIDE);
        }
    }

    /// <summary>Shows the widget window and re-asserts position (signed-in state).</summary>
    public void ShowWidget()
    {
        if (_nw.Handle != IntPtr.Zero)
        {
            Win32Interop.ShowWindow(_nw.Handle, Win32Interop.SW_SHOWNOACTIVATE);
            _nw.Reposition();
            _nw.AssertTopMost();
            _topmostTimer.Start();
            Redraw(); // also restarts _timer via ScheduleNextRedraw
        }
    }

    public void Dispose()
    {
        _topmostTimer.Stop();
        _topmostTimer.Dispose();
        _timer.Dispose();
        _nw.Dispose();
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private void Redraw()
    {
        _nw.Paint(Win32Interop.IsLightMode(), _data, _burnRate);
        ScheduleNextRedraw();
    }

    private void ScheduleNextRedraw()
    {
        _timer.Stop();
        if (_data == null) return;

        var delay = NextDisplayChange(_data);
        _timer.Interval = Math.Clamp((int)delay.TotalMilliseconds, 1_000, 60_000);
        _timer.Start();
    }

    /// <summary>
    /// Calculates how long until the displayed countdown text changes,
    /// so we only redraw when the display actually changes.
    /// </summary>
    private static TimeSpan NextDisplayChange(UsageData data)
    {
        var candidates = new List<TimeSpan>();
        if (data.SessionResetsAt.HasValue)
            candidates.Add(data.SessionResetIn);
        if (data.HasWeekly && data.WeeklyResetsAt.HasValue)
            candidates.Add(data.WeeklyResetIn);

        if (candidates.Count == 0) return TimeSpan.FromMinutes(1);

        var minNext = TimeSpan.MaxValue;
        foreach (var span in candidates)
        {
            var next = NextChangeForSpan(span);
            if (next < minNext) minNext = next;
        }
        return minNext;
    }

    private static TimeSpan NextChangeForSpan(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return TimeSpan.FromMinutes(1);

        if (remaining.TotalDays >= 1)
        {
            // Display: "Xd" — changes on each whole day boundary
            var fracDay = remaining - TimeSpan.FromDays((int)remaining.TotalDays);
            return fracDay > TimeSpan.Zero ? fracDay : TimeSpan.FromDays(1);
        }
        if (remaining.TotalHours >= 1)
        {
            // Display: "Xh" — changes on each whole hour boundary
            var fracHour = remaining - TimeSpan.FromHours((int)remaining.TotalHours);
            return fracHour > TimeSpan.Zero ? fracHour : TimeSpan.FromHours(1);
        }
        // Display: "Xm" — changes on each whole minute boundary
        var fracMin = remaining - TimeSpan.FromMinutes((int)remaining.TotalMinutes);
        return fracMin > TimeSpan.FromSeconds(1) ? fracMin : TimeSpan.FromMinutes(1);
    }

    // ── Render logic (static, called from WidgetNativeWindow.Paint) ───────────

    internal static void Render(Graphics g, int w, int h, bool light, UsageData? data, BurnRateTracker? burnRate = null)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.TextRenderingHint  = TextRenderingHint.AntiAliasGridFit;

        // Background: near-opaque so the idle widget stays readable. Seeing what's
        // behind it is handled by the hover-fade (alpha→0), not by a translucent bg.
        int bgAlpha = light ? 245 : 235;
        using var bgPath  = RoundedRect(new RectangleF(0, 0, w - 1, h - 1), 4);
        using (var bgBrush = new SolidBrush(Color.FromArgb(bgAlpha, BgColor(light))))
            g.FillPath(bgBrush, bgPath);

        if (light)
        {
            using var border = new Pen(Color.FromArgb(40, 0, 0, 0), 1);
            g.DrawPath(border, bgPath);
        }

        var textClr  = TextColor(light);
        var trackClr = TrackColor(light);

        // Left accent strip: top = session urgency, bottom = weekly urgency
        if (data != null)
        {
            using (var sa = new SolidBrush(FillColor(data.SessionPercent)))
                g.FillRectangle(sa, 0, Row1Y - 1, AccentW, BarH + 2);
            if (data.HasWeekly)
                using (var wa = new SolidBrush(FillColor(data.WeeklyPercent)))
                    g.FillRectangle(wa, 0, Row2Y - 1, AccentW, BarH + 2);
        }

        if (data == null)
        {
            using var loadFmt = new StringFormat
            {
                Alignment     = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
            };
            using var fb = new SolidBrush(Color.FromArgb(120, textClr)); // dimmer than ERR
            using var ff = new Font("Segoe UI", 8f);
            g.DrawString("–",       ff, fb, new RectangleF(PadL,          h / 2f - 8, 16,      14), loadFmt);
            g.DrawString("load…",   ff, fb, new RectangleF(PadL + 18,     h / 2f - 8, w - 22,  14), loadFmt);
            return;
        }

        int effectiveBarW = BarW;

        // Compute ETA: only show in Row1 when ETA < reset window (limit before reset)
        string? etaText = null;
        Color?  etaColor = null;
        if (burnRate != null)
        {
            var eta = burnRate.EstimateToFull(data.SessionPercent);
            if (eta.HasValue && data.SessionResetsAt.HasValue)
            {
                var resetIn = data.SessionResetIn;
                if (eta.Value < resetIn)
                {
                    // Limit will be hit before the session resets — show as warning
                    etaText  = FormatEta(eta.Value);
                    etaColor = data.SessionPercent >= 90 ? FillCrit : FillWarn;
                }
            }
        }

        DrawRow(g, Row1Y, "5h", data.SessionPercent, data.SessionResetIn, data.SessionPaceState,
                textClr, trackClr, light, effectiveBarW, data.SessionExpectedPercent,
                etaText, etaColor);

        if (data.HasWeekly)
        {
            // Opus: show "3d 3h · Op 7" — time and Opus combined in the extra field.
            // Expanded textW (+20px) so the combined string fits without widening the window.
            string? opusSuffix = data.HasOpus
                ? (data.WeeklyResetIn > TimeSpan.Zero
                    ? $"{FormatSpanShort(data.WeeklyResetIn)} · Op {data.OpusPercent!.Value:0}"
                    : $"Op {data.OpusPercent!.Value:0}")
                : null;
            int row2TextW = data.HasOpus ? TextW + 20 : TextW;
            DrawRow(g, Row2Y, "7d", data.WeeklyPercent, data.WeeklyResetIn, data.WeeklyPaceState,
                    textClr, trackClr, light, effectiveBarW, data.WeeklyExpectedPercent,
                    opusSuffix, null, row2TextW);
        }
        else
        {
            DrawRow(g, Row2Y, "7d", -1, TimeSpan.Zero, PaceState.OnPace,
                    textClr, trackClr, light, effectiveBarW, 0, null, null);
        }
    }

    private static string FormatEta(TimeSpan eta)
    {
        if (eta.TotalHours >= 1)
            return $"~{(int)eta.TotalHours}h{eta.Minutes:D2}m";
        return $"~{eta.Minutes}m";
    }

    private static void DrawRow(Graphics g, int rowY, string label,
                                double pct, TimeSpan resetIn, PaceState pace,
                                Color textClr, Color trackClr, bool light, int barW, double expectedPct,
                                string? extraText, Color? extraColor, int textW = TextW)
    {
        int contentX = PadL;

        // Label ("5h" / "7d")
        using var labelFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var labelBrush = new SolidBrush(pct >= 0 ? FillColor(pct) : Color.FromArgb(60, textClr));
        using var centerFmt = new StringFormat
        {
            Alignment     = StringAlignment.Near,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString(label, labelFont, labelBrush,
                     new RectangleF(contentX, rowY, LabelW, BarH), centerFmt);

        int barX = contentX + LabelW + LabelBarGap;

        if (pct >= 0)
        {
            DrawSolidBar(g, barX, rowY, pct, trackClr, light, barW, expectedPct);

            int textX = barX + barW + BarTextGap;

            string pctStr  = $"{pct:0}%";
            string timeStr = FormatSpanShort(resetIn);

            using var boldFont  = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            using var timeFont  = new Font("Segoe UI", 8f);
            using var pctBrush  = new SolidBrush(FillColor(pct));
            using var timeBrush = new SolidBrush(Color.FromArgb(210, textClr)); // brighter = more prominent

            // NoClip so the trailing "%" is never truncated by the field edge.
            using var pctFmt = new StringFormat
            {
                Alignment     = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                FormatFlags   = StringFormatFlags.NoClip,
            };
            // Time centered between the percentage and the pace glyph.
            using var timeFmt = new StringFormat
            {
                Alignment     = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags   = StringFormatFlags.NoClip,
            };

            // Fixed-width pct field so the time column aligns across both rows;
            // 36px holds "100%" without clipping.
            // Time field: sized to the widest possible string ("23h 59m") so "m" never clips.
            const int pctFieldW = 36;
            using var measureFont = new Font("Segoe UI", 8f);
            int timeFieldW = (int)Math.Ceiling(g.MeasureString("23h 59m", measureFont).Width) + 2;
            int glyphX  = textX + textW - 12;
            int glyphCY = rowY + BarH / 2;

            if (extraText != null)
            {
                // For ETA (Row1): extraText replaces the time field entirely.
                // For Opus (Row2): extraText already contains "timeStr · Op N" combined.
                // Drawn right-aligned before the glyph with a 7px gap so ETA glyph has breathing room.
                using var extraFont  = new Font("Segoe UI", 7.5f);
                using var extraBrush = new SolidBrush(extraColor ?? Color.FromArgb(210, textClr));
                int extraRight = glyphX - 7;
                int extraLeft  = textX + pctFieldW;
                using var extraFmt = new StringFormat
                {
                    Alignment     = StringAlignment.Far,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags   = StringFormatFlags.NoClip,
                };
                g.DrawString(pctStr, boldFont, pctBrush,
                             new RectangleF(textX, rowY, pctFieldW, BarH), pctFmt);
                g.DrawString(extraText, extraFont, extraBrush,
                             new RectangleF(extraLeft, rowY, extraRight - extraLeft, BarH), extraFmt);
            }
            else
            {
                int timeLeft  = textX + pctFieldW;
                int timeRight = timeLeft + timeFieldW;

                g.DrawString(pctStr, boldFont, pctBrush,
                             new RectangleF(textX, rowY, pctFieldW, BarH), pctFmt);
                g.DrawString(timeStr, timeFont, timeBrush,
                             new RectangleF(timeLeft, rowY, timeRight - timeLeft, BarH), timeFmt);
            }

            DrawPaceGlyph(g, glyphX, glyphCY, pace);
        }
        else
        {
            // No data — draw empty bar + dashes
            DrawSolidBar(g, barX, rowY, 0, trackClr, light, barW, 0);
            int textX = barX + barW + BarTextGap;
            using var textFont  = new Font("Segoe UI", 8f);
            using var textBrush = new SolidBrush(Color.FromArgb(60, textClr));
            g.DrawString("--", textFont, textBrush,
                         new RectangleF(textX, rowY, TextW, BarH), centerFmt);
        }
    }

    private static void DrawPaceGlyph(Graphics g, int cx, int cy, PaceState pace)
    {
        using (var shadow = new SolidBrush(Color.FromArgb(80, 0, 0, 0)))
            DrawGlyphShape(g, cx + 1, cy + 1, pace, shadow);

        using var brush = new SolidBrush(PaceGlyphColor(pace));
        DrawGlyphShape(g, cx, cy, pace, brush);
    }

    private static void DrawGlyphShape(Graphics g, int cx, int cy, PaceState pace, Brush b)
    {
        switch (pace)
        {
            case PaceState.Ahead:
                g.FillPolygon(b, new[] {
                    new PointF(cx,     cy - 4),
                    new PointF(cx + 5, cy + 4),
                    new PointF(cx - 5, cy + 4),
                });
                break;
            case PaceState.Under:
                g.FillPolygon(b, new[] {
                    new PointF(cx - 5, cy - 4),
                    new PointF(cx + 5, cy - 4),
                    new PointF(cx,     cy + 4),
                });
                break;
            case PaceState.OnPace:
                g.FillEllipse(b, cx - 3, cy - 3, 6, 6);
                break;
        }
    }

    private static Color PaceGlyphColor(PaceState p) => p switch
    {
        PaceState.Ahead  => FillCrit,
        PaceState.Under  => FillOk,
        _                => FillWarn,
    };

    private static void DrawSolidBar(Graphics g, int x, int y, double pct, Color trackClr, bool light, int barW, double expectedPct)
    {
        var barRect = new RectangleF(x, y, barW, BarH);

        // Track (background)
        using var path = RoundedRect(barRect, BarRadius);
        using (var trackBrush = new SolidBrush(trackClr))
            g.FillPath(trackBrush, path);

        // Grey on-pace band (±5% around expected usage) — reduced alpha so it's subtle
        if (expectedPct > 0)
        {
            float lo = (float)Math.Clamp(expectedPct - 5, 0, 100);
            float hi = (float)Math.Clamp(expectedPct + 5, 0, 100);
            float bandX = x + barW * lo / 100f;
            float bandW = barW * (hi - lo) / 100f;
            var bandState = g.Save();
            g.SetClip(new RectangleF(bandX, y, bandW, BarH), System.Drawing.Drawing2D.CombineMode.Intersect);
            using (var bandBrush = new SolidBrush(Color.FromArgb(60, 150, 150, 150)))
                g.FillPath(bandBrush, path);
            g.Restore(bandState);
        }

        // Fill (drawn on top of band so band disappears as usage exceeds expected)
        float fillW = barW * (float)Math.Clamp(pct, 0, 100) / 100f;
        if (fillW > 0)
        {
            var state = g.Save();
            g.SetClip(new RectangleF(x, y, fillW, BarH), CombineMode.Intersect);
            using (var fillBrush = new SolidBrush(FillColor(pct)))
                g.FillPath(fillBrush, path);
            g.Restore(state);
        }

        // Tick marks at 25%, 50%, 75%
        using var tickPen = new Pen(Color.FromArgb(90, 255, 255, 255), 1f);
        foreach (var t in new[] { 0.25f, 0.5f, 0.75f })
            g.DrawLine(tickPen, x + barW * t, y + 1, x + barW * t, y + BarH - 2);

        // Expected-pct notch: 1px wide, inset 2px from bar top/bottom — subtle "should be here" mark
        if (expectedPct > 0)
        {
            float tickX = x + barW * (float)expectedPct / 100f;
            tickX = Math.Clamp(tickX, x + 1, x + barW - 2);
            var markerClr = light ? Color.FromArgb(110, 30, 30, 30) : Color.FromArgb(110, 220, 220, 220);
            using var markerPen = new Pen(markerClr, 1f);
            g.DrawLine(markerPen, tickX, y + 2, tickX, y + BarH - 2);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, int radius)
    {
        float d    = radius * 2;
        var   path = new GraphicsPath();
        path.AddArc(r.Left,      r.Top,          d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top,          d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d,   d, d,   0, 90);
        path.AddArc(r.Left,      r.Bottom - d,   d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    private static string FormatSpanShort(TimeSpan ts)
    {
        var s = UsageData.FormatSpan(ts);
        return s == "--:--" ? "--" : s;
    }

    // ════════════════════════════════════════════════════════════════════════
    // WidgetNativeWindow — floating topmost layered window above the taskbar
    // ════════════════════════════════════════════════════════════════════════

    private sealed class WidgetNativeWindow : NativeWindow, IDisposable
    {
        private const int WM_MOUSEMOVE = 0x0200;

        private const int RestAlpha = 255; // fully opaque at rest; fades to 0 on hover

        private int _alpha       = RestAlpha;
        private int _targetAlpha = RestAlpha;

        private readonly System.Windows.Forms.Timer _fadeTimer;
        private readonly Action _repaint;

        public int CurrentAlpha => _alpha;

        internal WidgetNativeWindow(Action repaint)
        {
            _repaint = repaint;

            _fadeTimer = new System.Windows.Forms.Timer { Interval = 40 };
            _fadeTimer.Tick += OnFadeTick;

            var (x, y) = ComputePosition();
            var cp = new CreateParams
            {
                Width   = WidgetW,
                Height  = WidgetH,
                X       = x,
                Y       = y,
                Caption = "",
                Style   = unchecked((int)(Win32Interop.WS_POPUP | Win32Interop.WS_VISIBLE)),
                ExStyle = unchecked((int)(Win32Interop.WS_EX_LAYERED
                                        | Win32Interop.WS_EX_TOPMOST
                                        | Win32Interop.WS_EX_NOACTIVATE
                                        | Win32Interop.WS_EX_TOOLWINDOW)),
            };
            CreateHandle(cp);
            _fadeTimer.Start();
        }

        internal static (int x, int y) ComputePosition(int? currentX = null, int? currentY = null)
        {
            IntPtr shell  = Win32Interop.FindWindowW("Shell_TrayWnd", null);
            IntPtr notify = shell != IntPtr.Zero
                ? Win32Interop.FindWindowExW(shell, IntPtr.Zero, "TrayNotifyWnd", null)
                : IntPtr.Zero;

            Win32Interop.GetWindowRect(shell, out var taskbar);
            Win32Interop.RECT tray = default;
            if (notify != IntPtr.Zero)
                Win32Interop.GetWindowRect(notify, out tray);

            bool taskbarValid = shell != IntPtr.Zero && taskbar.Bottom > taskbar.Top;

            int x;
            if (notify != IntPtr.Zero && tray.Right > 0)
            {
                // Normal case: anchor to right edge of notification area
                x = tray.Right - WidgetW;
            }
            else if (taskbarValid && taskbar.Right > 0)
            {
                // Fallback: TrayNotifyWnd not found — anchor to taskbar right edge
                x = taskbar.Right - WidgetW;
            }
            else
            {
                // Both lookups failed — keep the current X to avoid jumping to 0
                x = currentX ?? 0;
            }

            x = Math.Max(0, x);
            // If taskbar rect is invalid, preserve the current Y rather than placing the
            // widget off-screen at a negative coordinate (taskbar.Top == 0 → y = -WidgetH).
            int y = taskbarValid
                ? taskbar.Top - WidgetH
                : currentY ?? Math.Max(0, taskbar.Top - WidgetH);
            y = Math.Max(0, y);
            return (x, y);
        }

        internal void AssertTopMost()
        {
            if (Handle == IntPtr.Zero) return;
            Win32Interop.SetWindowPos(Handle, Win32Interop.HWND_TOPMOST,
                                      0, 0, 0, 0,
                                      Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
        }

        internal void Reposition()
        {
            if (Handle == IntPtr.Zero) return;
            // Pass current X and Y so fallback paths can preserve position instead of jumping to 0/-WidgetH
            Win32Interop.GetWindowRect(Handle, out var current);
            var (x, y) = ComputePosition(current.Left, current.Top);
            Win32Interop.MoveWindow(Handle, x, y, WidgetW, WidgetH, false);
        }

        private void OnFadeTick(object? sender, EventArgs e)
        {
            const int step = 64; // 255/64 ≈ 4 ticks × 40ms = ~160ms fade

            if (_alpha < _targetAlpha)      _alpha = Math.Min(_targetAlpha, _alpha + step);
            else if (_alpha > _targetAlpha) _alpha = Math.Max(_targetAlpha, _alpha - step);

            // When fully faded: poll cursor; restore when mouse leaves our area
            if (_alpha == 0 && Handle != IntPtr.Zero)
            {
                Win32Interop.GetCursorPos(out var pt);
                Win32Interop.GetWindowRect(Handle, out var wr);
                bool inside = pt.X >= wr.Left && pt.X <= wr.Right
                           && pt.Y >= wr.Top  && pt.Y <= wr.Bottom;
                if (!inside) _targetAlpha = RestAlpha;
            }

            _repaint();
        }

        /// <summary>
        /// Renders widget content via UpdateLayeredWindow.
        /// SourceConstantAlpha drives the hover-fade effect; alpha=0 is naturally click-through.
        /// </summary>
        public void Paint(bool lightMode, UsageData? data, BurnRateTracker? burnRate = null)
        {
            if (Handle == IntPtr.Zero) return;

            var bmiHeader = new Win32Interop.BITMAPINFOHEADER
            {
                biSize        = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                biWidth       = WidgetW,
                biHeight      = -WidgetH,
                biPlanes      = 1,
                biBitCount    = 32,
                biCompression = 0,
            };

            var hdcScreen = Win32Interop.GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) return;

            var hdcMem  = Win32Interop.CreateCompatibleDC(hdcScreen);
            var hBitmap = Win32Interop.CreateDIBSection(hdcScreen, ref bmiHeader, 0,
                                                        out var pBits, IntPtr.Zero, 0);

            if (hdcMem == IntPtr.Zero || hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
            {
                if (hdcMem  != IntPtr.Zero) Win32Interop.DeleteDC(hdcMem);
                if (hBitmap != IntPtr.Zero) Win32Interop.DeleteObject(hBitmap);
                Win32Interop.ReleaseDC(IntPtr.Zero, hdcScreen);
                return;
            }

            var hOld = Win32Interop.SelectObject(hdcMem, hBitmap);

            try
            {
                using (var bmp = new Bitmap(WidgetW, WidgetH, WidgetW * 4, PixelFormat.Format32bppArgb, pBits))
                using (var gfx = Graphics.FromImage(bmp))
                {
                    TaskbarWidget.Render(gfx, WidgetW, WidgetH, lightMode, data, burnRate);
                }

                // Premultiply straight ARGB → premultiplied ARGB for UpdateLayeredWindow
                unsafe
                {
                    byte* p = (byte*)pBits;
                    int total = WidgetW * WidgetH * 4;
                    for (int i = 0; i < total; i += 4)
                    {
                        byte a = p[i + 3];
                        if (a == 0 || a == 255) continue;
                        p[i]     = (byte)((p[i]     * a) / 255);
                        p[i + 1] = (byte)((p[i + 1] * a) / 255);
                        p[i + 2] = (byte)((p[i + 2] * a) / 255);
                    }
                }

                var blend = new Win32Interop.BLENDFUNCTION
                {
                    BlendOp             = Win32Interop.AC_SRC_OVER,
                    BlendFlags          = 0,
                    SourceConstantAlpha = (byte)_alpha,  // drives hover-fade
                    AlphaFormat         = Win32Interop.AC_SRC_ALPHA,
                };
                var ptSrc = new Win32Interop.POINT { X = 0, Y = 0 };
                var sz    = new Win32Interop.SIZE  { cx = WidgetW, cy = WidgetH };

                Win32Interop.UpdateLayeredWindow(
                    Handle, hdcScreen, IntPtr.Zero, ref sz,
                    hdcMem, ref ptSrc, 0, ref blend, Win32Interop.ULW_ALPHA);
            }
            finally
            {
                Win32Interop.SelectObject(hdcMem, hOld);
                Win32Interop.DeleteObject(hBitmap);
                Win32Interop.DeleteDC(hdcMem);
                Win32Interop.ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEMOVE)
            {
                _targetAlpha = 0;
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            _fadeTimer.Stop();
            _fadeTimer.Dispose();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
