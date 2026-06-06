using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace ClaudeUsageMonitor;

internal sealed class Notifier
{
    private static readonly HttpClient _http = new();

    private readonly NotifyIcon _tray;

    public Notifier(NotifyIcon tray) { _tray = tray; }

    public void Dispatch(NotifyEvent ev)
    {
        var enabled = ev.Kind switch
        {
            NotifyKind.HighUsage    => Settings.Current.NotifyHighUsage,
            NotifyKind.LimitReached => Settings.Current.NotifyLimitReached,
            NotifyKind.Reset        => Settings.Current.NotifyReset,
            _                       => false,
        };

        if (!enabled) return;

        var message = ev.Kind switch
        {
            NotifyKind.HighUsage    => $"{ev.Quota} at {ev.Percent:0}% — resets in {ev.ResetText}.",
            NotifyKind.LimitReached => $"{ev.Quota} limit reached ({ev.Percent:0}%) — resets in {ev.ResetText}.",
            NotifyKind.Reset        => $"{ev.Quota} reset — quota renewed.",
            _                       => string.Empty,
        };

        var icon = ev.Kind == NotifyKind.LimitReached ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _tray.ShowBalloonTip(7000, "Claude Usage Monitor", message, icon);

        if (!string.IsNullOrEmpty(Settings.Current.NtfyTopic))
            _ = SendNtfyAsync(message);

        if (!string.IsNullOrEmpty(Settings.Current.ShellCommand))
            RunShell(message, ev);
    }

    public Task SendTestAsync()
    {
        var ev = new NotifyEvent(NotifyKind.HighUsage, "5h session", 90.0, "30m");
        Dispatch(ev);
        return Task.CompletedTask;
    }

    private static async Task SendNtfyAsync(string message)
    {
        try
        {
            var url = $"{Settings.Current.NtfyServer}/{Settings.Current.NtfyTopic}";
            var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(message, Encoding.UTF8),
            };
            request.Headers.Add("Title", "Claude Usage Monitor");
            await _http.SendAsync(request);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Notifier] ntfy POST failed: {ex.Message}");
        }
    }

    private static void RunShell(string message, NotifyEvent ev)
    {
        try
        {
            var substituted = Settings.Current.ShellCommand
                .Replace("{message}", message)
                .Replace("{event}", ev.Kind.ToString())
                .Replace("{percent}", ev.Percent.ToString("0"))
                .Replace("{quota}", ev.Quota);

            var psi = new ProcessStartInfo("cmd.exe", $"/c {substituted}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Notifier] Shell command failed: {ex.Message}");
        }
    }
}
