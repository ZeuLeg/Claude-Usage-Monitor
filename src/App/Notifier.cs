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
            NotifyKind.DepletionSoon => Settings.Current.NotifyDepletion,
            _                       => false,
        };

        if (!enabled) return;

        var channels = new StringBuilder("balloon");
        if (!string.IsNullOrEmpty(Settings.Current.NtfyTopic)) channels.Append("+ntfy");
        Logger.Info($"Notified {ev.Kind} {ev.Quota} via {channels}");

        var depletionMsg = ev.EtaToFull.HasValue
            ? $"At the current pace the 5h limit will be hit in ~{UsageData.FormatSpan(ev.EtaToFull.Value)} (resets in {ev.ResetText})."
            : $"{ev.Quota} will likely hit the limit before the session resets in {ev.ResetText}.";

        var message = ev.Kind switch
        {
            NotifyKind.HighUsage     => $"{ev.Quota} at {ev.Percent:0}% — resets in {ev.ResetText}.",
            NotifyKind.LimitReached  => $"{ev.Quota} limit reached ({ev.Percent:0}%) — resets in {ev.ResetText}.",
            NotifyKind.Reset         => $"{ev.Quota} reset — quota renewed.",
            NotifyKind.DepletionSoon => depletionMsg,
            _                        => string.Empty,
        };

        var title = ev.Kind == NotifyKind.DepletionSoon ? "Pace warning" : "Claude Usage Monitor";
        var icon = ev.Kind == NotifyKind.LimitReached ? ToolTipIcon.Warning : ToolTipIcon.Info;
        _tray.ShowBalloonTip(7000, title, message, icon);

        if (!string.IsNullOrEmpty(Settings.Current.NtfyTopic))
            _ = SendNtfyAsync(message);
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
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
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
}
