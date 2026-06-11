namespace ClaudeUsageMonitor;

internal static class NotificationsDialog
{
    private static readonly Color Accent  = Color.FromArgb(56, 189, 248);
    private static readonly Color Bg       = Color.FromArgb(24, 24, 27);
    private static readonly Color FieldBg  = Color.FromArgb(39, 39, 42);
    private static readonly Color Muted    = Color.FromArgb(140, 140, 150);

    public static void Show(Notifier notifier)
    {
        using var dlg = new Form
        {
            Text = "Notifications — Claude Usage Monitor",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Bg, ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f), TopMost = true,
            ShowInTaskbar = false,
            ClientSize = new Size(440, 368),
            StartPosition = FormStartPosition.CenterScreen,
        };

        // ── PHONE section ───────────────────────────────
        dlg.Controls.Add(SectionHeader("PHONE (OPTIONAL)", 16));

        dlg.Controls.Add(FieldLabel("ntfy topic", 46));
        var ntfyTopicBox = Field(130, 43, 290, Settings.Current.NtfyTopic);
        dlg.Controls.Add(ntfyTopicBox);

        dlg.Controls.Add(FieldLabel("ntfy server", 76));
        var ntfyServerBox = Field(130, 73, 290, Settings.Current.NtfyServer);
        dlg.Controls.Add(ntfyServerBox);

        dlg.Controls.Add(HelpText(
            "Subscribe to this topic in the ntfy app to get phone alerts.",
            28, 102, 400));

        dlg.Controls.Add(Divider(130, 440));

        // ── NOTIFY ME WHEN section ──────────────────────
        dlg.Controls.Add(SectionHeader("NOTIFY ME WHEN", 146));

        var chkHighUsage = new CheckBox
        {
            Text = "Usage reaches",
            Location = new Point(28, 182),
            Checked = Settings.Current.NotifyHighUsage,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
        };
        dlg.Controls.Add(chkHighUsage);

        var slider = new Slider
        {
            Location = new Point(150, 180),
            Width = 190,
            Minimum = 50,
            Maximum = 99,
            Value = Math.Clamp(Settings.Current.HighUsageThreshold, 50, 99),
            Enabled = chkHighUsage.Checked,
        };
        dlg.Controls.Add(slider);

        var pctLabel = new Label
        {
            Text = $"{slider.Value}%",
            Location = new Point(350, 183),
            ForeColor = Accent,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            AutoSize = true,
        };
        dlg.Controls.Add(pctLabel);

        slider.ValueChanged += (_, _) => pctLabel.Text = $"{slider.Value}%";
        chkHighUsage.CheckedChanged += (_, _) =>
        {
            slider.Enabled = chkHighUsage.Checked;
            pctLabel.ForeColor = chkHighUsage.Checked ? Accent : Muted;
        };
        if (!chkHighUsage.Checked) pctLabel.ForeColor = Muted;

        var chkLimitReached = new CheckBox
        {
            Text = "Limit reached (~100%)",
            Location = new Point(28, 218),
            Checked = Settings.Current.NotifyLimitReached,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
        };
        dlg.Controls.Add(chkLimitReached);

        var chkReset = new CheckBox
        {
            Text = "Quota resets",
            Location = new Point(28, 248),
            Checked = Settings.Current.NotifyReset,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
        };
        dlg.Controls.Add(chkReset);

        var chkDepletion = new CheckBox
        {
            Text = "Pace warning (will hit limit before reset)",
            Location = new Point(28, 278),
            Checked = Settings.Current.NotifyDepletion,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
        };
        dlg.Controls.Add(chkDepletion);

        // ── Footer buttons ──────────────────────────────
        var btnTest = FooterButton("Test Notification", 20, 324, 140, FieldBg, Color.White);
        btnTest.Click += async (_, _) =>
        {
            try { await notifier.SendTestAsync(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Test failed", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        using var tip = new ToolTip();
        tip.SetToolTip(btnTest,
            "Sends a test alert — always shows a Windows balloon;\nalso sends to phone via ntfy if a topic is configured.");
        dlg.Controls.Add(btnTest);

        var btnSave = FooterButton("Save", 250, 324, 80, Accent, Color.FromArgb(15, 15, 15));
        btnSave.Click += (_, _) =>
        {
            Settings.Current.NtfyTopic = ntfyTopicBox.Text;
            Settings.Current.NtfyServer = ntfyServerBox.Text;
            Settings.Current.NotifyHighUsage = chkHighUsage.Checked;
            Settings.Current.NotifyLimitReached = chkLimitReached.Checked;
            Settings.Current.NotifyReset = chkReset.Checked;
            Settings.Current.NotifyDepletion = chkDepletion.Checked;
            Settings.Current.HighUsageThreshold = slider.Value;
            Settings.Current.Save();
            dlg.Close();
        };
        dlg.Controls.Add(btnSave);

        var btnCancel = FooterButton("Cancel", 340, 324, 80, FieldBg, Color.White);
        btnCancel.Click += (_, _) => dlg.Close();
        dlg.Controls.Add(btnCancel);

        dlg.ShowDialog();
    }

    private static Label Divider(int y, int width) => new()
    {
        BackColor = Color.FromArgb(50, 50, 55),
        Location  = new Point(20, y),
        Size      = new Size(width, 1),
    };

    private static Label SectionHeader(string text, int y) => new()
    {
        Text = text,
        ForeColor = Accent,
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
        Location = new Point(20, y),
        AutoSize = true,
    };

    private static Label FieldLabel(string text, int y) => new()
    {
        Text = text,
        ForeColor = Color.White,
        Location = new Point(28, y),
        AutoSize = true,
    };

    private static Label HelpText(string text, int x, int y, int width) => new()
    {
        Text = text,
        ForeColor = Muted,
        Font = new Font("Segoe UI", 8.5f),
        Location = new Point(x, y),
        Size = new Size(width, 32),
    };

    private static TextBox Field(int x, int y, int width, string value) => new()
    {
        Location = new Point(x, y),
        Width = width,
        Text = value,
        BackColor = Color.FromArgb(39, 39, 42),
        ForeColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private static Button FooterButton(string text, int x, int y, int width, Color back, Color fore)
    {
        var btn = new Button
        {
            Text = text,
            Location = new Point(x, y),
            Width = width,
            Height = 30,
            BackColor = back,
            ForeColor = fore,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }
}
