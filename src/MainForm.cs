using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ClaudeUsageMonitor;

/// <summary>
/// Tray app. Reads OAuth token from Claude Code, fetches usage, displays icon.
/// </summary>
public sealed class MainForm : Form
{
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly UsageFetcher _fetcher;

    private const int PollIntervalMs = 120_000;  // 2 min base
    private const int MaxBackoffMs   = 300_000;  // 5 min cap

    private readonly SemaphoreSlim _pollGuard = new(1, 1);
    private UsageData? _lastData;
    private int _errors;
    private int _backoffMs = PollIntervalMs;
    private bool _tokenWarningShown;
    private IntPtr _trayIconHandle = IntPtr.Zero;
    private readonly CancellationTokenSource _cts = new();

    private Form? _widget;
    private TaskbarWidget? _taskbarWidget;
    private System.Windows.Forms.Timer? _resetTimer;
    private DateTime? _lastResetsAt;
    private ToolStripMenuItem? _updateMenuItem;
    private string? _pendingUpdateTag;

    // Registered once per process; non-zero on success (range 0xC000–0xFFFF)
    private static readonly uint _taskbarCreatedMsg =
        Win32Interop.RegisterWindowMessage("TaskbarCreated");

    private const int WM_POWERBROADCAST      = 0x0218;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int WM_SETTINGCHANGE       = 0x001A;

    private static readonly Color COk = Color.FromArgb(34, 197, 94);
    private static readonly Color CWarn = Color.FromArgb(251, 191, 36);
    private static readonly Color CCrit = Color.FromArgb(239, 68, 68);
    private static readonly Color CGray = Color.FromArgb(156, 163, 175);
    private static readonly Color CWeekly = Color.FromArgb(56, 189, 248); // cyan — weekly reference on session bar

    // over-pace = red (burning quota fast), under-pace = green (headroom), on-pace = yellow
    private static Color PaceColor(double diff) =>
        diff >= 5 ? CCrit : diff <= -5 ? COk : CWarn;

    public MainForm()
    {
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = Size.Empty;

        _fetcher = new UsageFetcher();

        var (initIcon, initHandle) = MakeIcon("...", CGray);
        _trayIconHandle = initHandle;
        _trayIcon = new NotifyIcon
        {
            Icon = initIcon,
            Text = "Claude Usage Monitor",
            ContextMenuStrip = BuildMenu(),
            Visible = true,
        };
        _trayIcon.DoubleClick += (_, _) => ShowDetails();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 120_000 }; // 2 min
        _pollTimer.Tick += (_, _) => FireAndForget(PollAsync);

        // Load event won't fire because SetVisibleCore(false) prevents visibility.
        // Use a one-shot timer to kick off initial work once the message loop is running.
        var startup = new System.Windows.Forms.Timer { Interval = 200 };
        startup.Tick += (_, _) => FireAndForget(async () =>
        {
            startup.Stop();
            startup.Dispose();
            await PollAsync();
            _pollTimer.Start();
            _taskbarWidget = new TaskbarWidget(_lastData);

            if (UpdateChecker.ShouldCheckToday())
            {
                var tag = await UpdateChecker.CheckAsync();
                UpdateChecker.RecordCheckTime();
                if (tag != null) ShowUpdateNotification(tag);
            }
        });

        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        startup.Start();
    }

    // ═══════════════════════════════════════
    // POLLING
    // ═══════════════════════════════════════

    private async Task PollAsync()
    {
        if (!_pollGuard.Wait(0)) return;

        try
        {
            var token = CredentialReader.GetAccessToken();
            if (token == null)
            {
                // Diagnostik: Warum kein Token?
                var userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "?";
                var credFile = Path.Combine(userProfile, ".claude", ".credentials.json");
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"[Poll] Credentials not found. File: {credFile}, Exists: {File.Exists(credFile)}");
#endif
                var diagMsg = "No OAuth token found.\nPlease run 'claude login'.";

                SetIcon("!", CCrit, diagMsg);
                if (!_tokenWarningShown)
                {
                    _tokenWarningShown = true;
                    _trayIcon.ShowBalloonTip(10000, "Claude Usage Monitor", diagMsg, ToolTipIcon.Warning);
                }
                return;
            }

            _tokenWarningShown = false;
            var data = await _fetcher.FetchAsync(token, _cts.Token);
            data = UsageData.MergeCarryForward(_lastData, data);
            if (data.WeeklyStale)
                Logger.Warn("seven_day missing from API response; carrying forward previous weekly data.");
            Logger.Info($"Poll OK — session={data.SessionPercent:0.0}% resets@{data.SessionResetsAt:u}, " +
                        $"weekly={data.WeeklyPercent:0.0}% hasWeekly={data.HasWeekly} stale={data.WeeklyStale} " +
                        $"resets@{data.WeeklyResetsAt:u}");
            _lastData = data;
            _errors = 0;
            _backoffMs = PollIntervalMs;
            _pollTimer.Interval = PollIntervalMs;

            ScheduleResetPoll(data);
            if (InvokeRequired)
                BeginInvoke(new Action(() => { SetIconVisual(data); RefreshWidget(); _taskbarWidget?.Update(data); }));
            else { SetIconVisual(data); RefreshWidget(); _taskbarWidget?.Update(data); }
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            _pollTimer.Stop(); // no point retrying until user re-auths
            Logger.Error("OAuth token expired or invalid.");
            SetIcon("AUTH", CCrit, "OAuth token expired.\nRun 'claude login'.");
            _trayIcon.ShowBalloonTip(8000, "Token expired",
                "Please run 'claude login' in the terminal.", ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            _errors++;
            _backoffMs = NextBackoff(_backoffMs);
            _pollTimer.Interval = _backoffMs;
            Logger.Error($"Poll failed (attempt {_errors}): {ex.GetType().Name}: {ex.Message}");
            if (ShouldShowStaleIcon(_lastData))
            {
                SetIconStale(_lastData!, "No connection — showing last known usage");
            }
            else
            {
                SetIcon("ERR", CCrit, $"Error: {ex.Message}");
                if (_errors >= 3)
                    _trayIcon.ShowBalloonTip(5000, "Error", ex.Message, ToolTipIcon.Error);
            }
        }
        finally
        {
            _pollGuard.Release();
        }
    }

    internal static int NextBackoff(int current) => Math.Min(current * 2, MaxBackoffMs);

    internal static bool ShouldShowStaleIcon(UsageData? last) => last != null;

    private void TriggerImmediateRecovery(string reason)
    {
        if (!IsHandleCreated) return;
        BeginInvoke(() =>
        {
            Logger.Info($"Immediate recovery: {reason}");
            _backoffMs = PollIntervalMs;
            _pollTimer.Interval = PollIntervalMs;
            FireAndForget(PollAsync);
        });
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            TriggerImmediateRecovery("power resume");
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Only react while we're actually failing, to avoid poll storms on routine changes.
        if (_errors > 0)
            TriggerImmediateRecovery("network address changed");
    }

    private void ScheduleResetPoll(UsageData data)
    {
        _resetTimer?.Stop();
        _resetTimer?.Dispose();
        _resetTimer = null;

        DateTime? nearest = null;
        foreach (var t in new[] { data.SessionResetsAt, data.HasWeekly ? data.WeeklyResetsAt : null })
        {
            if (!t.HasValue) continue;
            var localT = t.Value.ToLocalTime();
            if (localT <= DateTime.Now.AddSeconds(30)) continue;
            if (nearest == null || localT < nearest) nearest = localT;
        }

        if (nearest == null) return;
        if (nearest == _lastResetsAt) return;

        var fireIn = nearest.Value.AddSeconds(10) - DateTime.Now;
        if (fireIn.TotalMilliseconds > int.MaxValue) return;

        _lastResetsAt = nearest;
        _resetTimer = new System.Windows.Forms.Timer { Interval = (int)fireIn.TotalMilliseconds };
        _resetTimer.Tick += (_, _) =>
        {
            _resetTimer?.Stop();
            FireAndForget(PollAsync);
        };
        _resetTimer.Start();
    }

    // ═══════════════════════════════════════
    // MENU
    // ═══════════════════════════════════════

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        var show = new ToolStripMenuItem("Details") { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        show.Click += (_, _) => ShowDetails();
        m.Items.Add(show);

        m.Items.Add(new ToolStripSeparator());

        var refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += (_, _) => FireAndForget(PollAsync);
        m.Items.Add(refresh);

        var raw = new ToolStripMenuItem("Copy Status Text");
        raw.Click += (_, _) =>
        {
            if (_lastData?.TooltipText != null)
                Clipboard.SetText(_lastData.TooltipText);
        };
        m.Items.Add(raw);

        var openLog = new ToolStripMenuItem("Open Log Folder");
        openLog.Click += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Logger.LogDirectory,
                UseShellExecute = true,
            });
        m.Items.Add(openLog);

        var autostart = new ToolStripMenuItem("Start with Windows")
        {
            Checked      = IsAutostartEnabled(),
            CheckOnClick = true,
        };
        autostart.Click += (_, _) => SetAutostart(autostart.Checked);
        m.Items.Add(autostart);

        m.Items.Add(new ToolStripSeparator());

        _updateMenuItem = new ToolStripMenuItem("Check for Updates");
        _updateMenuItem.Click += (_, _) => FireAndForget(CheckUpdateClickAsync);
        m.Items.Add(_updateMenuItem);

        m.Items.Add(new ToolStripSeparator());

        var about = new ToolStripMenuItem("About");
        about.Click += (_, _) => ShowAbout();
        m.Items.Add(about);

        m.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { _cts.Cancel(); _trayIcon.Visible = false; Application.Exit(); };
        m.Items.Add(exit);

        return m;
    }

    // ═══════════════════════════════════════
    // AUTOSTART (HKCU Run key)
    // ═══════════════════════════════════════

    private const string AutostartName   = "ClaudeUsageMonitor";
    private const string AutostartRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static bool IsAutostartEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutostartRunKey);
        return key?.GetValue(AutostartName) != null;
    }

    private static void SetAutostart(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(AutostartRunKey, writable: true);
            if (key == null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe != null) key.SetValue(AutostartName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(AutostartName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Autostart toggle failed: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    // WIDGET
    // ═══════════════════════════════════════

    private void ShowDetails()
    {
        if (_widget != null && !_widget.IsDisposed)
        {
            _widget.Activate();
            _ = PollAsync();
            return;
        }

        _widget = new Form
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
        _widget.StartPosition = FormStartPosition.Manual;
        _widget.Location = new Point(screen.Right - 430, screen.Bottom - 120);

        _widget.FormClosed += (_, _) => _widget = null;

        if (_lastData != null)
            RefreshWidget();
        else
            _widget.Controls.Add(new Label
            {
                Text = "Loading...",
                Location = new Point(20, 15), Size = new Size(370, 22),
                ForeColor = CGray, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            });

        _widget.Show();

        if (_lastData == null)
            _ = PollAsync();
    }

    private static void ShowAbout()
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

    private void RefreshWidget()
    {
        if (_widget == null || _widget.IsDisposed || _lastData == null) return;

        _widget.SuspendLayout();
        while (_widget.Controls.Count > 0)
        {
            var c = _widget.Controls[0];
            _widget.Controls.RemoveAt(0);
            c.Dispose();
        }

        var d = _lastData;
        var updated = $" · {d.FetchedAt:HH:mm:ss}";

        int y = 15;
        // Session bar: colored session-pace marker + cyan weekly-reference marker (when available)
        var sessionSub = $"Reset: {d.SessionResetText} | {d.SessionPaceText}";
        if (d.HasWeekly)
            AddBar(_widget, ref y, "Session (5h)", d.SessionPercent, sessionSub,
                (d.SessionExpectedPercent, PaceColor(d.SessionPaceDiff)),
                (d.WeeklyExpectedPercent, CWeekly));
        else
            AddBar(_widget, ref y, "Session (5h)", d.SessionPercent, sessionSub + updated,
                (d.SessionExpectedPercent, PaceColor(d.SessionPaceDiff)));

        // Weekly bar: colored marker + pace status + updated time in subtitle
        if (d.HasWeekly)
        {
            var paceStatus = d.WeeklyPaceDiff >= 5 ? "ahead" : d.WeeklyPaceDiff <= -5 ? "under" : "on pace";
            var weeklySub = $"Reset: {d.WeeklyResetText} | {d.WeeklyPaceDiff:+0.0;-0.0;0.0}% {paceStatus}";
            AddBar(_widget, ref y, "Weekly (7d)", d.WeeklyPercent, weeklySub + updated,
                (d.WeeklyExpectedPercent, PaceColor(d.WeeklyPaceDiff)));
        }

        if (d.ExtraEnabled) AddBar(_widget, ref y, "Extra Usage", d.ExtraPercent,
            $"${d.ExtraUsedDollars:F2} / ${d.ExtraLimitDollars:F2}" + updated);

        // Size the widget exactly to content with a small bottom margin
        _widget.ClientSize = new Size(400, y + 8);

        _widget.ResumeLayout();
    }

    private static void AddBar(Form f, ref int y, string label, double pct, string sub,
        params (double Pct, Color Clr)[] markers)
    {
        var color = pct >= 90 ? CCrit : pct >= 75 ? CWarn : COk;

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

    // ═══════════════════════════════════════
    // UPDATE CHECKER
    // ═══════════════════════════════════════

    private async Task CheckUpdateClickAsync()
    {
        if (_pendingUpdateTag != null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = UpdateChecker.ReleasesUrl,
                UseShellExecute = true,
            });
            return;
        }

        var tag = await UpdateChecker.CheckAsync();
        UpdateChecker.RecordCheckTime();
        if (tag != null)
            ShowUpdateNotification(tag);
        else
            BeginInvoke(() => { if (_updateMenuItem != null) _updateMenuItem.Text = "Up to date"; });
    }

    private void ShowUpdateNotification(string tag)
    {
        _pendingUpdateTag = tag;
        if (InvokeRequired) { BeginInvoke(() => ShowUpdateNotification(tag)); return; }
        if (_updateMenuItem != null)
            _updateMenuItem.Text = $"Update available: v{tag}";
        _trayIcon.ShowBalloonTip(6000, "Update available",
            $"Claude Usage Monitor v{tag} is available. Use the tray menu to download.",
            ToolTipIcon.Info);
        Logger.Info($"Update available: v{tag}");
    }

    // ═══════════════════════════════════════
    // ASYNC HELPER
    // ═══════════════════════════════════════

    private static string TruncateTooltip(string text)
    {
        if (text.Length <= 127) return text;
        var cut = text.LastIndexOf('\n', 126);
        return cut > 0 ? text[..cut] : text[..127];
    }

    private static async void FireAndForget(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Unhandled] {ex}"); }
    }

    // ═══════════════════════════════════════
    // ICON RENDERING
    // ═══════════════════════════════════════

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
            var sc = data.SessionPercent >= 90 ? CCrit : data.SessionPercent >= 75 ? CWarn : COk;
            using var sp = new Pen(Color.FromArgb(sessA, sc), outerPen)
                { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(sp, outerInset, outerInset, outerD, outerD,
                      -90f, (float)(Math.Min(data.SessionPercent, 100) / 100.0 * 360.0));
        }

        // Weekly arc (inner) — steady cyan "reference" channel (matches the popup's
        // weekly markers); the outer session ring carries the green→yellow→red alarm.
        if (data.HasWeekly && data.WeeklyPercent > 0)
        {
            using var wp = new Pen(Color.FromArgb(weekA, CWeekly), innerPen)
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

    private void SetIcon(string text, Color color, string tooltip)
    {
        if (InvokeRequired) { BeginInvoke(() => SetIcon(text, color, tooltip)); return; }
        var old = _trayIcon.Icon;
        var oldHandle = _trayIconHandle;
        var (newIcon, newHandle) = MakeIcon(text, color);
        _trayIcon.Icon = newIcon;
        _trayIconHandle = newHandle;
        _trayIcon.Text = TruncateTooltip(tooltip);
        old?.Dispose();
        if (oldHandle != IntPtr.Zero) Win32Interop.DestroyIcon(oldHandle);
    }

    private void SetIconVisual(UsageData data)
    {
        if (InvokeRequired) { BeginInvoke(() => SetIconVisual(data)); return; }
        var old       = _trayIcon.Icon;
        var oldHandle = _trayIconHandle;
        var (newIcon, newHandle) = MakeIconVisual(data);
        _trayIcon.Icon      = newIcon;
        _trayIconHandle     = newHandle;
        _trayIcon.Text      = TruncateTooltip(data.TooltipText);
        old?.Dispose();
        if (oldHandle != IntPtr.Zero) Win32Interop.DestroyIcon(oldHandle);
    }

    private void SetIconStale(UsageData data, string reason)
    {
        if (InvokeRequired) { BeginInvoke(() => SetIconStale(data, reason)); return; }
        var old       = _trayIcon.Icon;
        var oldHandle = _trayIconHandle;
        var (newIcon, newHandle) = MakeIconVisual(data, dim: true);
        _trayIcon.Icon  = newIcon;
        _trayIconHandle = newHandle;
        _trayIcon.Text  = TruncateTooltip($"{reason}\nLast updated: {data.FetchedAt:HH:mm}");
        old?.Dispose();
        if (oldHandle != IntPtr.Zero) Win32Interop.DestroyIcon(oldHandle);
    }

    // ═══════════════════════════════════════
    // LIFECYCLE
    // ═══════════════════════════════════════

    protected override void WndProc(ref Message m)
    {
        // Re-embed the taskbar widget whenever Explorer restarts
        if (_taskbarCreatedMsg != 0 && m.Msg == (int)_taskbarCreatedMsg)
            _taskbarWidget?.Reattach();
        // Invalidate theme cache and redraw when user changes color scheme
        if (m.Msg == WM_SETTINGCHANGE)
        {
            var lParam = System.Runtime.InteropServices.Marshal.PtrToStringUni(m.LParam);
            if (lParam == "ImmersiveColorSet")
            {
                Win32Interop.InvalidateThemeCache();
                _taskbarWidget?.Reposition();
            }
        }
        // Reposition + re-poll after wake from standby (timer doesn't count sleep time)
        if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt32() == PBT_APMRESUMEAUTOMATIC)
        {
            _taskbarWidget?.Reposition();
            // The taskbar may not have finished re-laying out at this point.
            // Schedule a second reposition after 2 s so the widget doesn't
            // sit on top of the "show hidden icons" chevron once the taskbar settles.
            var retryTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            retryTimer.Tick += (_, _) =>
            {
                retryTimer.Stop();
                retryTimer.Dispose();
                _taskbarWidget?.Reposition();
            };
            retryTimer.Start();
            _backoffMs = PollIntervalMs;
            _pollTimer.Interval = PollIntervalMs;
            FireAndForget(PollAsync);
        }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
        base.OnFormClosing(e);
    }

    protected override void SetVisibleCore(bool value)
    {
        // Force HWND creation so WndProc receives broadcast messages
        // (TaskbarCreated, theme change) even though we stay invisible.
        if (!IsHandleCreated) CreateHandle();
        base.SetVisibleCore(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged; System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; _cts.Cancel(); _cts.Dispose(); _pollGuard.Dispose(); _widget?.Dispose(); _taskbarWidget?.Dispose(); _pollTimer?.Dispose(); _resetTimer?.Dispose(); _trayIcon?.Dispose(); _fetcher?.Dispose(); if (_trayIconHandle != IntPtr.Zero) Win32Interop.DestroyIcon(_trayIconHandle); }
        base.Dispose(disposing);
    }
}
