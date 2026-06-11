using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace ClaudeUsageMonitor;

/// <summary>
/// Floating detail card shown when the user hovers the widget.
/// Renders via UpdateLayeredWindow (same pattern as TaskbarWidget).
/// WS_EX_TRANSPARENT ensures mouse clicks pass through to whatever is behind the card.
/// </summary>
internal sealed class HoverCard : IDisposable
{
    // ── Layout ────────────────────────────────────────────────────────────────
    internal const int CardW  = 240;
    internal const int CardH  = 96;
    private  const int PadX   = 10;
    private  const int PadTop = 8;
    private  const int LineH  = 18;
    private  const int Radius = 6;

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly CardNativeWindow _nw;
    private bool _visible;
    private UsageData? _lastData;
    private BurnRateTracker? _lastBurnRate;

    public HoverCard() => _nw = new CardNativeWindow();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Positions the card so its bottom edge is 4px above the widget top, right-aligned.
    /// When the widget is at the top of the screen (taskbar on top), places the card
    /// 4px below the widget bottom instead. Paints with fresh data before showing.
    /// </summary>
    public void ShowAbove(int widgetLeft, int widgetTop, int widgetBottom, int widgetWidth,
                          bool light, UsageData data, BurnRateTracker? burnRate)
    {
        // Skip redundant MoveWindow + full GDI render when card is already visible
        // and the data reference hasn't changed (same 2-min poll object).
        if (_visible && ReferenceEquals(data, _lastData) && ReferenceEquals(burnRate, _lastBurnRate))
            return;

        _lastData     = data;
        _lastBurnRate = burnRate;

        int x = widgetLeft + widgetWidth - CardW;
        int y = widgetTop - CardH - 4;
        if (y < 0) y = widgetBottom + 4; // taskbar is at the top of the screen
        x = Math.Max(0, x);

        Win32Interop.MoveWindow(_nw.Handle, x, y, CardW, CardH, false);
        _nw.Paint(light, data, burnRate);
        if (!_visible)
        {
            Win32Interop.ShowWindow(_nw.Handle, Win32Interop.SW_SHOWNOACTIVATE);
            _nw.AssertTopMost();
            _visible = true;
        }
    }

    public void Hide()
    {
        if (_visible)
        {
            Win32Interop.ShowWindow(_nw.Handle, Win32Interop.SW_HIDE);
            _visible      = false;
            _lastData     = null;
            _lastBurnRate = null;
        }
    }

    public void Dispose() => _nw.Dispose();

    // ── Render (static — callable from tests) ─────────────────────────────────

    internal static void Render(Graphics g, int w, int h, bool light,
                                UsageData data, BurnRateTracker? burnRate = null)
    {
        g.Clear(Color.Transparent);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // Background
        int bgAlpha = light ? 248 : 238;
        var bg = Color.FromArgb(bgAlpha, light ? Color.FromArgb(0xF3, 0xF3, 0xF3)
                                                : Color.FromArgb(0x1C, 0x1C, 0x1C));
        using var bgPath  = RoundRect(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), Radius);
        using (var bgBrush = new SolidBrush(bg))
            g.FillPath(bgBrush, bgPath);

        // Border
        using var borderPen = new Pen(Color.FromArgb(light ? 45 : 60, 128, 128, 128), 1f);
        g.DrawPath(borderPen, bgPath);

        var textClr  = light ? Color.FromArgb(0x20, 0x20, 0x20) : Color.FromArgb(0xCC, 0xCC, 0xCC);
        var dimClr   = Color.FromArgb(light ? 110 : 110, textClr);
        var warnClr  = Color.FromArgb(251, 191, 36);
        var critClr  = Color.FromArgb(239, 68, 68);

        using var titleFont  = new Font("Segoe UI", 8.5f, FontStyle.Bold);
        using var labelFont  = new Font("Segoe UI", 7.5f, FontStyle.Bold);
        using var bodyFont   = new Font("Segoe UI", 8f);
        using var dimFont    = new Font("Segoe UI", 7.5f);

        // ── Title row ─────────────────────────────────────────────────────────
        int y = PadTop;
        using (var tb = new SolidBrush(textClr))
            g.DrawString("Claude Usage", titleFont, tb, PadX, y);

        if (!string.IsNullOrEmpty(data.PlanLabel))
        {
            // Right-aligned, dim
            float planW = g.MeasureString(data.PlanLabel, dimFont).Width;
            using var pb = new SolidBrush(dimClr);
            g.DrawString(data.PlanLabel, dimFont, pb, w - PadX - planW, y + 1);
        }

        y += LineH + 2; // divider gap

        // ── Divider ───────────────────────────────────────────────────────────
        using (var divPen = new Pen(Color.FromArgb(light ? 30 : 45, 128, 128, 128), 1f))
            g.DrawLine(divPen, PadX, y, w - PadX, y);

        y += 4;

        // ── Session row ───────────────────────────────────────────────────────
        var sessionClr = FillColor(data.SessionPercent);
        DrawCardRow(g, y, w, light,
            label: "Session",
            pctStr: $"{data.SessionPercent:0}%",
            pctColor: sessionClr,
            detail: BuildSessionDetail(data, burnRate, warnClr, critClr, out Color? etaColor),
            detailColor: etaColor ?? dimClr,
            textClr, dimClr, labelFont, bodyFont, dimFont);

        y += LineH;

        // ── Weekly row ────────────────────────────────────────────────────────
        if (data.HasWeekly)
        {
            string weekDetail = BuildWeeklyDetail(data);
            DrawCardRow(g, y, w, light,
                label: "Weekly",
                pctStr: $"{data.WeeklyPercent:0}%",
                pctColor: FillColor(data.WeeklyPercent),
                detail: weekDetail,
                detailColor: dimClr,
                textClr, dimClr, labelFont, bodyFont, dimFont);
            y += LineH;
        }

        // ── Burn rate row (only when available) ───────────────────────────────
        var rate = burnRate?.RatePerHour;
        if (rate.HasValue && rate.Value > 0)
        {
            string rateStr = "+" + rate.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%/h";
            DrawCardRow(g, y, w, light,
                label: "Burn rate",
                pctStr: rateStr,
                pctColor: rate.Value >= 20 ? critClr : warnClr,
                detail: string.Empty,
                detailColor: dimClr,
                textClr, dimClr, labelFont, bodyFont, dimFont);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildSessionDetail(UsageData data, BurnRateTracker? burnRate,
                                             Color warnClr, Color critClr, out Color? etaColor)
    {
        etaColor = null;
        var parts = new List<string>();

        if (data.SessionResetsAt.HasValue)
        {
            var local = data.SessionResetsAt.Value.ToLocalTime();
            parts.Add($"resets {local:HH:mm}");
        }

        if (burnRate != null)
        {
            var eta = burnRate.EstimateToFull(data.SessionPercent);
            if (eta.HasValue && data.SessionResetsAt.HasValue && eta.Value < data.SessionResetIn)
            {
                string etaStr = eta.Value.TotalHours >= 1
                    ? $"limit in ~{(int)eta.Value.TotalHours}h {eta.Value.Minutes}m"
                    : $"limit in ~{eta.Value.Minutes}m";
                parts.Add(etaStr);
                etaColor = data.SessionPercent >= 90 ? critClr : warnClr;
            }
        }

        return string.Join(" • ", parts);
    }

    private static string BuildWeeklyDetail(UsageData data)
    {
        var parts = new List<string>();

        if (data.WeeklyResetsAt.HasValue)
        {
            var local = data.WeeklyResetsAt.Value.ToLocalTime();
            parts.Add("resets " + local.ToString("ddd HH:mm", CultureInfo.InvariantCulture));
        }

        if (data.HasOpus)
            parts.Add($"Opus {data.OpusPercent!.Value:0}%");

        return string.Join(" • ", parts);
    }

    private static void DrawCardRow(Graphics g, int y, int w, bool light,
                                    string label, string pctStr, Color pctColor,
                                    string detail, Color detailColor,
                                    Color textClr, Color dimClr,
                                    Font labelFont, Font bodyFont, Font dimFont)
    {
        // Label (left, dim)
        using (var lb = new SolidBrush(dimClr))
            g.DrawString(label, labelFont, lb, PadX, y + 1);

        // Percentage (mid-left, colored)
        float labelW = g.MeasureString(label, labelFont).Width;
        float pctX = PadX + labelW + 4;
        using (var pb = new SolidBrush(pctColor))
            g.DrawString(pctStr, bodyFont, pb, pctX, y);

        // Detail (right-aligned)
        if (!string.IsNullOrEmpty(detail))
        {
            float detW = g.MeasureString(detail, dimFont).Width;
            float detX = w - PadX - detW;
            using (var db = new SolidBrush(detailColor))
                g.DrawString(detail, dimFont, db, detX, y + 1);
        }
    }

    private static Color FillColor(double pct) =>
        pct >= 90 ? Color.FromArgb(239, 68, 68)
      : pct >= 75 ? Color.FromArgb(251, 191, 36)
      :             Color.FromArgb(34, 197, 94);

    private static GraphicsPath RoundRect(RectangleF r, int radius)
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

    // ════════════════════════════════════════════════════════════════════════
    // CardNativeWindow — layered, topmost, transparent-to-clicks
    // ════════════════════════════════════════════════════════════════════════

    private sealed class CardNativeWindow : NativeWindow, IDisposable
    {
        internal CardNativeWindow()
        {
            var cp = new CreateParams
            {
                Width   = CardW,
                Height  = CardH,
                X       = -CardW,   // off-screen until Show is called
                Y       = -CardH,
                Caption = "",
                Style   = unchecked((int)(Win32Interop.WS_POPUP)),
                ExStyle = unchecked((int)(Win32Interop.WS_EX_LAYERED
                                        | Win32Interop.WS_EX_TOPMOST
                                        | Win32Interop.WS_EX_NOACTIVATE
                                        | Win32Interop.WS_EX_TOOLWINDOW
                                        | Win32Interop.WS_EX_TRANSPARENT)),
            };
            CreateHandle(cp);
        }

        internal void AssertTopMost()
        {
            if (Handle == IntPtr.Zero) return;
            Win32Interop.SetWindowPos(Handle, Win32Interop.HWND_TOPMOST,
                                      0, 0, 0, 0,
                                      Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE);
        }

        internal void Paint(bool light, UsageData data, BurnRateTracker? burnRate)
        {
            if (Handle == IntPtr.Zero) return;

            var bmiHeader = new Win32Interop.BITMAPINFOHEADER
            {
                biSize     = System.Runtime.InteropServices.Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>(),
                biWidth    = CardW,
                biHeight   = -CardH,
                biPlanes   = 1,
                biBitCount = 32,
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
                _ = Win32Interop.ReleaseDC(IntPtr.Zero, hdcScreen);
                return;
            }

            var hOld = Win32Interop.SelectObject(hdcMem, hBitmap);
            try
            {
                using (var bmp = new Bitmap(CardW, CardH, CardW * 4, PixelFormat.Format32bppArgb, pBits))
                using (var gfx = Graphics.FromImage(bmp))
                {
                    HoverCard.Render(gfx, CardW, CardH, light, data, burnRate);
                }

                // Premultiply straight → premultiplied ARGB for UpdateLayeredWindow
                unsafe
                {
                    byte* p = (byte*)pBits;
                    int total = CardW * CardH * 4;
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
                    SourceConstantAlpha = 255,
                    AlphaFormat         = Win32Interop.AC_SRC_ALPHA,
                };
                var ptSrc = new Win32Interop.POINT { X = 0, Y = 0 };
                var sz    = new Win32Interop.SIZE  { cx = CardW, cy = CardH };

                Win32Interop.UpdateLayeredWindow(
                    Handle, hdcScreen, IntPtr.Zero, ref sz,
                    hdcMem, ref ptSrc, 0, ref blend, Win32Interop.ULW_ALPHA);
            }
            finally
            {
                Win32Interop.SelectObject(hdcMem, hOld);
                Win32Interop.DeleteObject(hBitmap);
                Win32Interop.DeleteDC(hdcMem);
                _ = Win32Interop.ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
