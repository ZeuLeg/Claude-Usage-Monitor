using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Drawing.Imaging;

namespace ClaudeUsageMonitor;

/// <summary>
/// A widget embedded directly in the Windows taskbar (next to the system tray).
/// Uses Win32 reparenting (WS_CHILD + SetParent) and UpdateLayeredWindow for
/// per-pixel alpha rendering. Falls back gracefully if embedding fails.
/// </summary>
internal sealed class TaskbarWidget : IDisposable
{
    // ── Layout constants ────────────────────────────────────────────────────
    private const int WidgetW     = 290;
    private const int WidgetH     = 46;
    private const int PadL        = 6;   // left padding
    private const int LabelW      = 20;  // "5h" / "7d" text width
    private const int LabelBarGap = 4;
    private const int BarW        = 152; // solid bar width
    private const int BarH        = 13;  // solid bar height
    private const int BarTextGap  = 5;
    private const int PadR        = 4;
    // TextW = 290 - 6 - 20 - 4 - 152 - 5 - 4 = 99
    private const int TextW       = WidgetW - PadL - LabelW - LabelBarGap - BarW - BarTextGap - PadR;
    private const int Row1Y       = 6;   // top y of row-1 bar (session)
    private const int Row2Y       = 27;  // top y of row-2 bar (weekly)
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
    private UsageData? _data;

    public bool IsEmbedded => _nw.Embedded;

    public ContextMenuStrip? ContextMenu
    {
        get => _nw.ContextMenu;
        set => _nw.ContextMenu = value;
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    public TaskbarWidget(UsageData? initialData = null)
    {
        _nw = new WidgetNativeWindow(WidgetW, WidgetH);

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) => Redraw();

        if (initialData != null)
            Update(initialData);
        else
            Redraw(); // show loading state
    }

    // ── Public API ───────────────────────────────────────────────────────────

    public void Update(UsageData data)
    {
        _data = data;
        Redraw();
    }

    /// <summary>
    /// Called when the TaskbarCreated message is received (Explorer restarted).
    /// Recreates the window and re-embeds it in the new taskbar.
    /// </summary>
    public void Reattach()
    {
        _nw.Reattach();
        if (_nw.Embedded)
            Redraw();
    }

    /// <summary>
    /// Called on resume from standby. Re-runs position math so the widget
    /// doesn't drift over the "show hidden icons" arrow after the taskbar relays out.
    /// </summary>
    public void Reposition()
    {
        if (_nw.RepositionInTaskbar())
            Redraw();
    }

    public void Dispose()
    {
        _timer.Dispose();
        _nw.Dispose();
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private void Redraw()
    {
        if (!_nw.Embedded) return;
        if (!_nw.RepositionInTaskbar()) return; // hidden due to no taskbar room
        _nw.Paint(Win32Interop.IsLightMode(), _data);
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

    internal static void Render(Graphics g, int w, int h, bool light, UsageData? data)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode      = SmoothingMode.AntiAlias;
        g.TextRenderingHint  = TextRenderingHint.ClearTypeGridFit;

        // Background: nearly invisible in dark mode (hit-testable); soft plate in light mode.
        int bgAlpha = light ? 220 : 1;
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

        if (data == null)
        {
            using var fb = new SolidBrush(textClr);
            using var ff = new Font("Segoe UI", 8f);
            g.DrawString("...", ff, fb, new RectangleF(PadL, h / 2f - 7, w, 14));
            return;
        }

        bool compact = w < WidgetW;
        // In compact mode the bar fills available space; pct text replaces the countdown+glyph.
        // barW = w - PadL - LabelW - LabelBarGap - BarTextGap - pctTextW - PadR
        //      = w - 6   - 20    - 4           - 5          - 26       - 4     = w - 65
        int effectiveBarW = compact ? Math.Max(w - 65, 10) : BarW;

        DrawRow(g, Row1Y, "5h", data.SessionPercent, data.SessionResetIn, data.SessionPaceState,
                textClr, trackClr, effectiveBarW, compact);

        if (data.HasWeekly)
            DrawRow(g, Row2Y, "7d", data.WeeklyPercent, data.WeeklyResetIn, data.WeeklyPaceState,
                    textClr, trackClr, effectiveBarW, compact);
        else
            DrawRow(g, Row2Y, "7d", -1, TimeSpan.Zero, PaceState.OnPace,
                    textClr, trackClr, effectiveBarW, compact);
    }

    private static void DrawRow(Graphics g, int rowY, string label,
                                double pct, TimeSpan resetIn, PaceState pace,
                                Color textClr, Color trackClr, int barW, bool compact)
    {
        int contentX = PadL;

        // Label ("5h" / "7d")
        using var labelFont = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var labelBrush = new SolidBrush(pct >= 0 ? textClr : Color.FromArgb(60, textClr));
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
            DrawSolidBar(g, barX, rowY, pct, trackClr, barW);

            int textX = barX + barW + BarTextGap;
            if (compact)
            {
                // Compact: only "NN%" — no countdown or pace glyph
                using var textFont  = new Font("Segoe UI", 8f);
                using var textBrush = new SolidBrush(textClr);
                g.DrawString($"{pct:0}%", textFont, textBrush,
                             new RectangleF(textX, rowY, 28, BarH), centerFmt);
            }
            else
            {
                // Full: "42% - 2h 14m" + pace glyph
                string txt = $"{pct:0}% - {FormatSpanShort(resetIn)}";
                using var textFont  = new Font("Segoe UI", 8f);
                using var textBrush = new SolidBrush(textClr);
                g.DrawString(txt, textFont, textBrush,
                             new RectangleF(textX, rowY, TextW - 20, BarH), centerFmt);

                int glyphX  = textX + TextW - 12;
                int glyphCY = rowY + BarH / 2;
                DrawPaceGlyph(g, glyphX, glyphCY, pace);
            }
        }
        else
        {
            // No data — draw empty bar + dashes
            DrawSolidBar(g, barX, rowY, 0, trackClr, barW);
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

    private static void DrawSolidBar(Graphics g, int x, int y, double pct, Color trackClr, int barW)
    {
        var barRect = new RectangleF(x, y, barW, BarH);

        // Track (background)
        using var path = RoundedRect(barRect, BarRadius);
        using (var trackBrush = new SolidBrush(trackClr))
            g.FillPath(trackBrush, path);

        // Fill
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
    // WidgetNativeWindow — low-level Win32 window that embeds in the taskbar
    // ════════════════════════════════════════════════════════════════════════

    private sealed class WidgetNativeWindow : NativeWindow, IDisposable
    {
        private const int WM_RBUTTONUP  = 0x0205;
        private const int MinGapToShow  = 100; // hide when taskbar gap < this
        private const int FullWidgetW   = WidgetW;

        private int          _w;
        private readonly int _h;
        public bool Embedded { get; private set; }
        public ContextMenuStrip? ContextMenu { get; set; }

        public WidgetNativeWindow(int w, int h)
        {
            _w = w; _h = h;

            var cp = new CreateParams
            {
                // Start as POPUP (top-level); will be changed to CHILD after creation
                Style   = unchecked((int)(Win32Interop.WS_POPUP | Win32Interop.WS_VISIBLE)),
                ExStyle = unchecked((int)(Win32Interop.WS_EX_TOOLWINDOW
                                        | Win32Interop.WS_EX_LAYERED
                                        | Win32Interop.WS_EX_NOACTIVATE)),
                Width   = w,
                Height  = h,
                X       = -2000, // off-screen until embedded
                Y       = -2000,
                Caption = "",
            };

            try
            {
                CreateHandle(cp);
                Embedded = TryEmbedInTaskbar();
            }
            catch
            {
                Embedded = false;
            }
        }

        private bool TryEmbedInTaskbar()
        {
            if (Handle == IntPtr.Zero) return false;

            // Find the main taskbar and the tray notification area
            var taskbar = Win32Interop.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return false;

            var trayNotify = Win32Interop.FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (trayNotify == IntPtr.Zero) return false;

            if (!Win32Interop.GetWindowRect(trayNotify, out var trayRect) ||
                !Win32Interop.GetWindowRect(taskbar,    out var taskbarRect))
                return false;

            // Rewrite window style: POPUP → CHILD | CLIPSIBLINGS
            var style = Win32Interop.GetWindowLong(Handle, Win32Interop.GWL_STYLE);
            style = (style & ~unchecked((int)Win32Interop.WS_POPUP))
                  | unchecked((int)(Win32Interop.WS_CHILD | Win32Interop.WS_CLIPSIBLINGS));
            Win32Interop.SetWindowLong(Handle, Win32Interop.GWL_STYLE, style);

            // Reparent into the taskbar
            var result = Win32Interop.SetParent(Handle, taskbar);
            if (result == IntPtr.Zero) return false;

            // Position: left of tray area, vertically centered in taskbar.
            // Use available gap to decide width; hide if there is no room.
            int taskbarH = taskbarRect.Height;
            int gap      = trayRect.Left - taskbarRect.Left;

            if (gap < MinGapToShow)
            {
                Win32Interop.ShowWindow(Handle, Win32Interop.SW_HIDE);
                return true; // successfully embedded, but hidden
            }

            _w = Math.Min(gap - 4, FullWidgetW);
            int x = trayRect.Left - taskbarRect.Left - _w;
            int y = (taskbarH - _h) / 2;

            Win32Interop.MoveWindow(Handle, x, y, _w, _h, true);
            Win32Interop.ShowWindow(Handle, Win32Interop.SW_SHOWNOACTIVATE);

            return true;
        }

        /// <summary>
        /// Renders the widget content via UpdateLayeredWindow.
        /// Creates a 32-bit top-down DIB section, draws with GDI+ directly into it,
        /// then hands the result to the window manager for compositing.
        /// </summary>
        public void Paint(bool lightMode, UsageData? data)
        {
            if (Handle == IntPtr.Zero) return;

            var bmiHeader = new Win32Interop.BITMAPINFOHEADER
            {
                biSize        = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                biWidth       = _w,
                biHeight      = -_h,  // negative = top-down scan order
                biPlanes      = 1,
                biBitCount    = 32,
                biCompression = 0,    // BI_RGB
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
                // GDI+ draws directly into the DIB section memory pointed to by pBits.
                // stride = _w * 4 (32-bit ARGB, top-down)
                using (var bmp = new Bitmap(_w, _h, _w * 4, PixelFormat.Format32bppArgb, pBits))
                using (var gfx = Graphics.FromImage(bmp))
                {
                    TaskbarWidget.Render(gfx, _w, _h, lightMode, data);
                }

                // UpdateLayeredWindow with AC_SRC_ALPHA requires premultiplied ARGB.
                // GDI+ writes straight ARGB, so premultiply each non-trivial pixel in-place.
                unsafe
                {
                    byte* p = (byte*)pBits;
                    int total = _w * _h * 4;
                    for (int i = 0; i < total; i += 4)
                    {
                        byte a = p[i + 3];
                        if (a == 0 || a == 255) continue;
                        p[i]     = (byte)((p[i]     * a) / 255);
                        p[i + 1] = (byte)((p[i + 1] * a) / 255);
                        p[i + 2] = (byte)((p[i + 2] * a) / 255);
                    }
                }

                // GDI+ objects are disposed above — safe to call UpdateLayeredWindow now.
                var blend = new Win32Interop.BLENDFUNCTION
                {
                    BlendOp              = Win32Interop.AC_SRC_OVER,
                    BlendFlags           = 0,
                    SourceConstantAlpha  = 255,
                    AlphaFormat          = Win32Interop.AC_SRC_ALPHA,
                };
                var ptSrc = new Win32Interop.POINT { X = 0, Y = 0 };
                var sz    = new Win32Interop.SIZE   { cx = _w, cy = _h };

                // IntPtr.Zero for pptDst: keep the position managed by MoveWindow
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

        /// <summary>
        /// Re-runs the position calculation and moves the window to match the
        /// current taskbar layout. Used on resume from standby.
        /// Returns true if the window was successfully repositioned.
        /// </summary>
        // Returns true if visible (caller should paint), false if hidden or failed.
        public bool RepositionInTaskbar()
        {
            if (Handle == IntPtr.Zero || !Embedded) return false;

            var taskbar = Win32Interop.FindWindowW("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero) return false;

            var trayNotify = Win32Interop.FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (trayNotify == IntPtr.Zero) return false;

            if (!Win32Interop.GetWindowRect(trayNotify, out var trayRect) ||
                !Win32Interop.GetWindowRect(taskbar,    out var taskbarRect))
                return false;

            int gap = trayRect.Left - taskbarRect.Left;

            if (gap < MinGapToShow)
            {
                Win32Interop.ShowWindow(Handle, Win32Interop.SW_HIDE);
                return false;
            }

            _w = Math.Min(gap - 4, FullWidgetW);
            int x = trayRect.Left - taskbarRect.Left - _w;
            int y = (taskbarRect.Height - _h) / 2;
            Win32Interop.MoveWindow(Handle, x, y, _w, _h, true);
            Win32Interop.ShowWindow(Handle, Win32Interop.SW_SHOWNOACTIVATE);
            return true;
        }

        /// <summary>
        /// Re-embeds the widget after Explorer restarts (TaskbarCreated message).
        /// The old child window was destroyed with Shell_TrayWnd, so we release the
        /// stale handle reference and create a fresh window before re-embedding.
        /// </summary>
        public void Reattach()
        {
            Embedded = false;
            if (Handle != IntPtr.Zero)
                ReleaseHandle(); // don't call DestroyWindow on an already-destroyed handle

            var cp = new CreateParams
            {
                Style   = unchecked((int)(Win32Interop.WS_POPUP | Win32Interop.WS_VISIBLE)),
                ExStyle = unchecked((int)(Win32Interop.WS_EX_TOOLWINDOW
                                        | Win32Interop.WS_EX_LAYERED
                                        | Win32Interop.WS_EX_NOACTIVATE)),
                Width   = _w,
                Height  = _h,
                X       = -2000,
                Y       = -2000,
                Caption = "",
            };

            try
            {
                CreateHandle(cp);
                Embedded = TryEmbedInTaskbar();
            }
            catch
            {
                Embedded = false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_RBUTTONUP && ContextMenu != null)
            {
                var lp = m.LParam.ToInt32();
                var pt = new Win32Interop.POINT
                {
                    X = (short)(lp & 0xFFFF),
                    Y = (short)((lp >> 16) & 0xFFFF),
                };
                Win32Interop.ClientToScreen(Handle, ref pt);
                ContextMenu.Show(pt.X, pt.Y);
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
                DestroyHandle();
        }
    }
}
