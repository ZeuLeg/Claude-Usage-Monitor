namespace ClaudeUsageMonitor;

/// <summary>
/// Tray app. Reads OAuth token from Claude Code, fetches usage, displays icon.
/// </summary>
public sealed class MainForm : Form
{
    private readonly NotifyIcon _trayIcon;
    private readonly TrayIconRenderer _tray;
    private readonly UsagePoller _poller;

    private bool _tokenWarningShown;
    private bool _authWarningShown;
    private readonly Notifier _notifier;
    private readonly NotificationEvaluator _evaluator;
    private TaskbarWidget? _taskbarWidget;
    private ToolStripMenuItem? _updateMenuItem;
    private string? _pendingUpdateTag;
    private readonly System.Windows.Forms.Timer _singleClickTimer;

    // Registered once per process; non-zero on success (range 0xC000–0xFFFF)
    private static readonly uint _taskbarCreatedMsg =
        Win32Interop.RegisterWindowMessage("TaskbarCreated");

    private const int WM_POWERBROADCAST      = 0x0218;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const int WM_SETTINGCHANGE       = 0x001A;

    public MainForm()
    {
        Settings.Load();
        if (Enum.TryParse<LogLevel>(Settings.Current.LogLevel, ignoreCase: true, out var lvl))
            Logger.MinLevel = lvl;
        ShowInTaskbar = false;
        WindowState = FormWindowState.Minimized;
        FormBorderStyle = FormBorderStyle.None;
        Opacity = 0;
        Size = Size.Empty;

        // Create poller first so BuildMenu lambdas can capture a non-null reference.
        _poller = new UsagePoller(this);

        // Defer single-click Refresh so a double-click can cancel it before it fires.
        _singleClickTimer = new System.Windows.Forms.Timer
            { Interval = SystemInformation.DoubleClickTime };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            FireAndForget(_poller.PollAsync);
        };

        _trayIcon = new NotifyIcon
        {
            Text = "Claude Usage Monitor",
            ContextMenuStrip = BuildMenu(),
        };
        _tray = new TrayIconRenderer(_trayIcon, this);
        _tray.ShowText("...", Palette.Gray, "Claude Usage Monitor");
        _trayIcon.Visible = true;

        _notifier = new Notifier(_trayIcon);
        _evaluator = new NotificationEvaluator
        {
            HighUsageThreshold      = Settings.Current.HighUsageThreshold,
            HighUsageCooldownMinutes = Settings.Current.HighUsageCooldownMinutes,
        };

        // Left single click → Refresh (deferred); double click cancels the deferred
        // Refresh and opens the Notifications dialog instead.
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _singleClickTimer.Start();   // (re)start; fires after DoubleClickTime
        };
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _singleClickTimer.Stop();    // cancel pending Refresh
                NotificationsDialog.Show(_notifier);
                _evaluator.HighUsageThreshold      = Settings.Current.HighUsageThreshold;
                _evaluator.HighUsageCooldownMinutes = Settings.Current.HighUsageCooldownMinutes;
            }
        };

        _poller.Updated += data =>
        {
            _tray.ShowUsage(data);
            _taskbarWidget?.Update(data, _poller.BurnRate);
            _tokenWarningShown = false;
            _authWarningShown = false;
            foreach (var ev in _evaluator.Evaluate(data))
                _notifier.Dispatch(ev);
        };
        _poller.TokenMissing += diag =>
        {
            _tray.ShowText("!", Palette.Crit, diag);
            if (!_tokenWarningShown)
            {
                _tokenWarningShown = true;
                _trayIcon.ShowBalloonTip(10000, "Claude Usage Monitor", diag, ToolTipIcon.Warning);
            }
        };
        _poller.AuthExpired += () =>
        {
            _tray.ShowText("AUTH", Palette.Crit, "OAuth token expired.\nRun 'claude login'.");
            if (!_authWarningShown)
            {
                _authWarningShown = true;
                _trayIcon.ShowBalloonTip(8000, "Token expired",
                    "Please run 'claude login' in the terminal.", ToolTipIcon.Warning);
            }
        };
        _poller.Failed += (last, msg, count, isConnectivity) =>
        {
            if (UsagePoller.ShouldShowStaleIcon(last))
                _tray.ShowStale(last!, "No connection — showing last known usage");
            else if (isConnectivity)
                _tray.ShowText("---", Palette.Gray, "Waiting for connection…");
            else
            {
                _tray.ShowText("ERR", Palette.Crit, $"Error: {msg}");
                if (count >= 3)
                    _trayIcon.ShowBalloonTip(5000, "Error", msg, ToolTipIcon.Error);
            }
        };

        // Load event won't fire because SetVisibleCore(false) prevents visibility.
        // Use a one-shot timer to kick off initial work once the message loop is running.
        var startup = new System.Windows.Forms.Timer { Interval = 200 };
        startup.Tick += (_, _) => FireAndForget(async () =>
        {
            startup.Stop();
            startup.Dispose();
            // Warm up ClaudeCodeInfo off the UI thread so the first poll doesn't block
            _ = Task.Run(() => _ = ClaudeCodeInfo.Version);
            await _poller.PollAsync();
            _poller.Start();
            _taskbarWidget = new TaskbarWidget(_poller.LastData);

            if (UpdateChecker.ShouldCheckToday())
            {
                var tag = await UpdateChecker.CheckAsync();
                UpdateChecker.RecordCheckTime();
                if (tag != null) ShowUpdateNotification(tag);
            }
        });

        startup.Start();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke(() => _taskbarWidget?.Reposition()); return; }
        _taskbarWidget?.Reposition();
    }

    // ═══════════════════════════════════════
    // MENU
    // ═══════════════════════════════════════

    private ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();

        var refresh = new ToolStripMenuItem("Refresh");
        refresh.Click += (_, _) => FireAndForget(_poller.PollAsync);
        m.Items.Add(refresh);

        var raw = new ToolStripMenuItem("Copy Status Text");
        raw.Click += (_, _) =>
        {
            if (_poller.LastData?.TooltipText != null)
                Clipboard.SetText(_poller.LastData.TooltipText);
        };
        m.Items.Add(raw);

        var diag = new ToolStripMenuItem("Diagnostics");

        // Log level radio group
        var logLevels = new[] { LogLevel.Error, LogLevel.Warn, LogLevel.Info, LogLevel.Debug };
        foreach (var level in logLevels)
        {
            var item = new ToolStripMenuItem(level.ToString())
            {
                Checked = Logger.MinLevel == level,
                CheckOnClick = false,
            };
            item.Click += (_, _) =>
            {
                Logger.MinLevel = level;
                Settings.Current.LogLevel = level.ToString();
                Settings.Current.Save();
                // Update all log level items' Checked state
                foreach (var li in diag.DropDownItems.OfType<ToolStripMenuItem>())
                {
                    if (li.Tag is LogLevel lv)
                        li.Checked = lv == level;
                }
            };
            item.Tag = level;
            diag.DropDownItems.Add(item);
        }

        diag.DropDownItems.Add(new ToolStripSeparator());

        var openLogFolder = new ToolStripMenuItem("Open Log Folder");
        openLogFolder.Click += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = Logger.LogDirectory,
                UseShellExecute = false,
            })?.Dispose();
        diag.DropDownItems.Add(openLogFolder);

        var copyDiag = new ToolStripMenuItem("Copy Diagnostics");
        copyDiag.Click += (_, _) => Clipboard.SetText(Diagnostics.BuildReport());
        diag.DropDownItems.Add(copyDiag);

        m.Items.Add(diag);

        var autostart = new ToolStripMenuItem("Start with Windows")
        {
            Checked      = AutostartManager.IsEnabled(),
            CheckOnClick = true,
        };
        autostart.Click += (_, _) => AutostartManager.Set(autostart.Checked);
        m.Items.Add(autostart);

        m.Items.Add(new ToolStripSeparator());

        _updateMenuItem = new ToolStripMenuItem("Check for Updates");
        _updateMenuItem.Click += (_, _) => FireAndForget(CheckUpdateClickAsync);
        m.Items.Add(_updateMenuItem);

        m.Items.Add(new ToolStripSeparator());

        var notifications = new ToolStripMenuItem("Notifications…");
        notifications.Click += (_, _) =>
        {
            NotificationsDialog.Show(_notifier);
            _evaluator.HighUsageThreshold       = Settings.Current.HighUsageThreshold;
            _evaluator.HighUsageCooldownMinutes  = Settings.Current.HighUsageCooldownMinutes;
        };
        m.Items.Add(notifications);

        var about = new ToolStripMenuItem("About");
        about.Click += (_, _) => AboutDialog.Show();
        m.Items.Add(about);

        m.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => { _poller.Dispose(); _trayIcon.Visible = false; Application.Exit(); };
        m.Items.Add(exit);

        return m;
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

    private static async void FireAndForget(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Error($"[FireAndForget] {ex}"); }
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
            _poller.RecoverNow("power broadcast");
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
        if (disposing)
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _singleClickTimer.Dispose();
            _poller.Dispose();
            _taskbarWidget?.Dispose();
            _tray.Dispose();
            _trayIcon?.Dispose();
        }
        base.Dispose(disposing);
    }
}
