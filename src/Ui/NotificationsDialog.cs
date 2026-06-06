namespace ClaudeUsageMonitor;

internal static class NotificationsDialog
{
    public static void Show(Notifier notifier)
    {
        using var dlg = new Form
        {
            Text = "Notifications — Claude Usage Monitor",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(24, 24, 27), ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f), TopMost = true,
            ShowInTaskbar = false,
            ClientSize = new Size(420, 340),
            StartPosition = FormStartPosition.CenterScreen,
        };

        dlg.Controls.Add(new Label
        {
            Text = "ntfy Topic:",
            Location = new Point(20, 18),
            AutoSize = true,
        });

        var ntfyTopicBox = new TextBox
        {
            Location = new Point(130, 15),
            Width = 270,
            Text = Settings.Current.NtfyTopic,
        };
        dlg.Controls.Add(ntfyTopicBox);

        dlg.Controls.Add(new Label
        {
            Text = "ntfy Server:",
            Location = new Point(20, 50),
            AutoSize = true,
        });

        var ntfyServerBox = new TextBox
        {
            Location = new Point(130, 47),
            Width = 270,
            Text = Settings.Current.NtfyServer,
        };
        dlg.Controls.Add(ntfyServerBox);

        dlg.Controls.Add(new Label
        {
            Text = "Subscribe to the same topic in the ntfy app to receive notifications on your phone.",
            ForeColor = Color.FromArgb(140, 140, 150),
            Font = new Font("Segoe UI", 8.5f),
            Location = new Point(20, 80),
            Size = new Size(380, 32),
        });

        dlg.Controls.Add(new Label
        {
            Text = "Shell Command:",
            Location = new Point(20, 120),
            AutoSize = true,
        });

        var shellBox = new TextBox
        {
            Location = new Point(130, 117),
            Width = 270,
            Text = Settings.Current.ShellCommand,
        };
        dlg.Controls.Add(shellBox);

        dlg.Controls.Add(new Label
        {
            Text = "(use {message}, {event}, {percent}, {quota})",
            ForeColor = Color.FromArgb(100, 100, 110),
            Font = new Font("Segoe UI", 8f),
            Location = new Point(130, 142),
            AutoSize = true,
        });

        var chkHighUsage = new CheckBox
        {
            Text = "Notify on high usage (90%)",
            Location = new Point(20, 165),
            Checked = Settings.Current.NotifyHighUsage,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
        };
        dlg.Controls.Add(chkHighUsage);

        var chkLimitReached = new CheckBox
        {
            Text = "Notify on limit reached (~100%)",
            Location = new Point(20, 195),
            Checked = Settings.Current.NotifyLimitReached,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
        };
        dlg.Controls.Add(chkLimitReached);

        var chkReset = new CheckBox
        {
            Text = "Notify on reset",
            Location = new Point(20, 225),
            Checked = Settings.Current.NotifyReset,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            AutoSize = true,
        };
        dlg.Controls.Add(chkReset);

        var btnTest = new Button
        {
            Text = "Test Notification",
            Location = new Point(20, 265),
            Width = 140,
            Height = 28,
            BackColor = Color.FromArgb(39, 39, 42),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        btnTest.Click += (_, _) => _ = notifier.SendTestAsync();
        dlg.Controls.Add(btnTest);

        var btnSave = new Button
        {
            Text = "Save",
            Location = new Point(230, 265),
            Width = 80,
            Height = 28,
            BackColor = Color.FromArgb(56, 189, 248),
            ForeColor = Color.FromArgb(15, 15, 15),
            FlatStyle = FlatStyle.Flat,
        };
        btnSave.Click += (_, _) =>
        {
            Settings.Current.NtfyTopic = ntfyTopicBox.Text;
            Settings.Current.NtfyServer = ntfyServerBox.Text;
            Settings.Current.ShellCommand = shellBox.Text;
            Settings.Current.NotifyHighUsage = chkHighUsage.Checked;
            Settings.Current.NotifyLimitReached = chkLimitReached.Checked;
            Settings.Current.NotifyReset = chkReset.Checked;
            Settings.Current.Save();
            dlg.Close();
        };
        dlg.Controls.Add(btnSave);

        var btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(320, 265),
            Width = 80,
            Height = 28,
            BackColor = Color.FromArgb(39, 39, 42),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
        };
        btnCancel.Click += (_, _) => dlg.Close();
        dlg.Controls.Add(btnCancel);

        dlg.ShowDialog();
    }
}
