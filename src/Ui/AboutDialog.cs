namespace ClaudeUsageMonitor;

/// <summary>The "About" dialog: name, version and a link to the repo.</summary>
internal static class AboutDialog
{
    public static void Show()
    {
        var version = System.Reflection.Assembly
            .GetExecutingAssembly().GetName().Version;
        var verStr = version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";

        using var dlg = new Form
        {
            Text = "About Claude Usage Monitor",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(24, 24, 27), ForeColor = Color.White,
            TopMost = true,
            ShowInTaskbar = false,
            ClientSize = new Size(400, 110),
            StartPosition = FormStartPosition.CenterScreen,
        };
        // dlg.Font is not tracked by Form.Dispose(); dispose both fonts explicitly.
        var dlgFont   = new Font("Segoe UI", 10f);
        var titleFont = new Font("Segoe UI", 11f, FontStyle.Bold);
        dlg.Font = dlgFont;
        dlg.Disposed += (_, _) => { dlgFont.Dispose(); titleFont.Dispose(); };

        dlg.Controls.Add(new Label
        {
            Text = "Claude Usage Monitor",
            Font = titleFont,
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
            })?.Dispose();
        dlg.Controls.Add(link);

        dlg.ShowDialog();
    }
}
