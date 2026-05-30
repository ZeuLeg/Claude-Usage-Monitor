namespace ClaudeUsageMonitor;

/// <summary>The "About" dialog: name, version and a link to the repo.</summary>
internal static class AboutDialog
{
    public static void Show()
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
            ClientSize = new Size(400, 110),
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
