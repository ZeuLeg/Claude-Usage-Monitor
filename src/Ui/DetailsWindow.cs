using System.Drawing.Drawing2D;

namespace ClaudeUsageMonitor;

/// <summary>
/// Manages the floating details popup window (per-bar usage breakdown + About dialog).
/// </summary>
internal sealed class DetailsWindow
{
    private Form? _form;

    public void Show(UsageData? current, Func<Task> refresh)
    {
        if (_form != null && !_form.IsDisposed)
        {
            _form.Activate();
            _ = refresh();
            return;
        }

        _form = new Form
        {
            Text = "Claude Usage",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(24, 24, 27), ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f), TopMost = true,
            ShowInTaskbar = false,
            ClientSize = new Size(400, 60),
        };

        // Position near system tray (bottom-right)
        var screen = Screen.PrimaryScreen!.WorkingArea;
        _form.StartPosition = FormStartPosition.Manual;
        _form.Location = new Point(screen.Right - 430, screen.Bottom - 120);

        _form.FormClosed += (_, _) => _form = null;

        if (current != null)
            Render(current);
        else
            _form.Controls.Add(new Label
            {
                Text = "Loading...",
                Location = new Point(20, 15), Size = new Size(370, 22),
                ForeColor = Palette.Gray, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            });

        _form.Show();

        if (current == null)
            _ = refresh();
    }

    public void Update(UsageData data)
    {
        if (_form == null || _form.IsDisposed) return;
        Render(data);
    }

    private void Render(UsageData d)
    {
        if (_form == null || _form.IsDisposed) return;

        _form.SuspendLayout();
        while (_form.Controls.Count > 0)
        {
            var c = _form.Controls[0];
            _form.Controls.RemoveAt(0);
            c.Dispose();
        }

        var updated = $" · {d.FetchedAt:HH:mm:ss}";

        int y = 15;
        // Session bar: colored session-pace marker + cyan weekly-reference marker (when available)
        var sessionSub = $"Reset: {d.SessionResetText} | {d.SessionPaceText}";
        if (d.HasWeekly)
            AddBar(_form, ref y, "Session (5h)", d.SessionPercent, sessionSub,
                (d.SessionExpectedPercent, Palette.Pace(d.SessionPaceDiff)),
                (d.WeeklyExpectedPercent, Palette.Weekly));
        else
            AddBar(_form, ref y, "Session (5h)", d.SessionPercent, sessionSub + updated,
                (d.SessionExpectedPercent, Palette.Pace(d.SessionPaceDiff)));

        // Weekly bar: colored marker + pace status + updated time in subtitle
        if (d.HasWeekly)
        {
            var paceStatus = d.WeeklyPaceDiff >= 5 ? "ahead" : d.WeeklyPaceDiff <= -5 ? "under" : "on pace";
            var weeklySub = $"Reset: {d.WeeklyResetText} | {d.WeeklyPaceDiff:+0.0;-0.0;0.0}% {paceStatus}";
            AddBar(_form, ref y, "Weekly (7d)", d.WeeklyPercent, weeklySub + updated,
                (d.WeeklyExpectedPercent, Palette.Pace(d.WeeklyPaceDiff)));
        }

        if (d.ExtraEnabled) AddBar(_form, ref y, "Extra Usage", d.ExtraPercent,
            $"${d.ExtraUsedDollars:F2} / ${d.ExtraLimitDollars:F2}" + updated);

        // Size the widget exactly to content with a small bottom margin
        _form.ClientSize = new Size(400, y + 8);

        _form.ResumeLayout();
    }

    private static void AddBar(Form f, ref int y, string label, double pct, string sub,
        params (double Pct, Color Clr)[] markers)
    {
        var color = pct >= 90 ? Palette.Crit : pct >= 75 ? Palette.Warn : Palette.Ok;

        f.Controls.Add(new Label
        {
            Text = $"{label}: {pct:0.0}%",
            Location = new Point(20, y), Size = new Size(370, 22),
            ForeColor = color, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
        });
        y += 24;

        var bar = new Panel { Location = new Point(20, y), Size = new Size(370, 14), BackColor = Color.FromArgb(45, 45, 50) };
        bar.Paint += (_, e) =>
        {
            var w = (int)(bar.Width * Math.Min(pct, 100) / 100);
            if (w > 0) { using var b = new SolidBrush(color); e.Graphics.FillRectangle(b, 0, 0, w, bar.Height); }

            // Draw each pace marker as a colored vertical line
            foreach (var (mPct, mClr) in markers)
            {
                if (mPct < 0) continue;
                var mx = (int)(bar.Width * Math.Min(mPct, 100) / 100);
                using var pen = new Pen(Color.FromArgb(210, mClr), 2);
                e.Graphics.DrawLine(pen, mx, 0, mx, bar.Height);
            }
        };
        f.Controls.Add(bar);
        y += 18;

        f.Controls.Add(new Label
        {
            Text = sub, Location = new Point(20, y), Size = new Size(370, 18),
            ForeColor = Color.FromArgb(140, 140, 150), Font = new Font("Segoe UI", 8.5f),
        });
        y += 18;
    }

    public static void ShowAbout()
    {
        var version = System.Reflection.Assembly
            .GetExecutingAssembly().GetName().Version;
        var verStr = version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";

        var dlg = new Form
        {
            Text = "About Claude Usage Monitor",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(24, 24, 27), ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f), TopMost = true,
            ShowInTaskbar = false,
            ClientSize = new Size(300, 110),
            StartPosition = FormStartPosition.CenterScreen,
        };

        dlg.Controls.Add(new Label
        {
            Text = "Claude Usage Monitor",
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.White,
            Location = new Point(20, 18),
            AutoSize = true,
        });

        dlg.Controls.Add(new Label
        {
            Text = $"Version {verStr}",
            ForeColor = Color.FromArgb(140, 140, 150),
            Location = new Point(20, 44),
            AutoSize = true,
        });

        var link = new LinkLabel
        {
            Text = "github.com/ZeuLeg/Claude-Usage-Monitor",
            Location = new Point(20, 68),
            AutoSize = true,
            BackColor = Color.Transparent,
            LinkColor = Color.FromArgb(56, 189, 248),
            ActiveLinkColor = Color.White,
        };
        link.LinkClicked += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/ZeuLeg/Claude-Usage-Monitor",
                UseShellExecute = true,
            });
        dlg.Controls.Add(link);

        dlg.ShowDialog();
    }
}
