namespace ClaudeUsageMonitor;

/// <summary>
/// Owns the 2-minute poll cycle, exponential backoff, power/network recovery, and the
/// reset-aligned one-shot timer. Raises events for UI side-effects so the poller stays
/// decoupled from WinForms controls.
/// </summary>
internal sealed class UsagePoller : IDisposable
{
    // ═══════════════════════════════════════
    // EVENTS
    // ═══════════════════════════════════════

    public event Action<UsageData>? Updated;
    public event Action<string>? TokenMissing;
    public event Action? AuthExpired;
    public event Action<UsageData?, string, int>? Failed;

    // ═══════════════════════════════════════
    // CONSTANTS & FIELDS
    // ═══════════════════════════════════════

    private const int PollIntervalMs = 120_000;  // 2 min base
    private const int MaxBackoffMs   = 300_000;  // 5 min cap

    private readonly SemaphoreSlim _pollGuard = new(1, 1);
    private readonly UsageFetcher _fetcher;
    private readonly TokenProvider _tokenProvider;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private readonly Control _invoker;
    private readonly CancellationTokenSource _cts = new();

    private UsageData? _lastData;
    private string? _lastPollSignature;
    private int _errors;
    private int _backoffMs = PollIntervalMs;
    private System.Windows.Forms.Timer? _resetTimer;
    private DateTime? _lastResetsAt;

    public UsageData? LastData => _lastData;

    // ═══════════════════════════════════════
    // CONSTRUCTOR
    // ═══════════════════════════════════════

    public UsagePoller(Control invoker)
    {
        _invoker = invoker;
        _fetcher = new UsageFetcher();
        _tokenProvider = new TokenProvider();

        _pollTimer = new System.Windows.Forms.Timer { Interval = PollIntervalMs };
        _pollTimer.Tick += (_, _) => FireAndForget(PollAsync);
    }

    // ═══════════════════════════════════════
    // PUBLIC API
    // ═══════════════════════════════════════

    public void Start()
    {
        _pollTimer.Start();
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public void RecoverNow(string reason)
    {
        if (!_invoker.IsHandleCreated) return;
        _invoker.BeginInvoke(() =>
        {
            _backoffMs = PollIntervalMs;
            _pollTimer.Interval = PollIntervalMs;
            Logger.Info($"Immediate recovery: {reason}");
            FireAndForget(PollAsync);
        });
    }

    public void Dispose()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _cts.Cancel();
        _cts.Dispose();
        _pollGuard.Dispose();
        _pollTimer.Dispose();
        _resetTimer?.Dispose();
        _fetcher.Dispose();
    }

    // ═══════════════════════════════════════
    // POLLING
    // ═══════════════════════════════════════

    public async Task PollAsync()
    {
        if (!_pollGuard.Wait(0)) return;

        try
        {
            var (token, hasCredentials) = await _tokenProvider.GetValidAccessTokenAsync(_cts.Token);
            if (token == null)
            {
                if (!hasCredentials)
                {
#if DEBUG
                    var userProfile = Environment.GetEnvironmentVariable("USERPROFILE") ?? "?";
                    var credFile = Path.Combine(userProfile, ".claude", ".credentials.json");
                    System.Diagnostics.Debug.WriteLine($"[Poll] Credentials not found. File: {credFile}, Exists: {File.Exists(credFile)}");
#endif
                    TokenMissing?.Invoke("No OAuth token found.\nPlease run 'claude login'.");
                }
                else
                {
                    // Credentials exist but refresh failed — ask user to login
                    Logger.Error("Token refresh failed; credentials may be revoked.");
                    AuthExpired?.Invoke();
                }
                return;
            }

            var data = await _fetcher.FetchAsync(token, _cts.Token);
            data = UsageData.MergeCarryForward(_lastData, data);
            if (data.WeeklyStale)
                Logger.Warn("seven_day missing from API response; carrying forward previous weekly data.");
            var sig = $"{data.SessionPercent:0}|{data.HasWeekly}|{data.WeeklyPercent:0}|{data.ExtraEnabled}";
            var pollMsg = $"Poll OK — session={data.SessionPercent:0.0}% resets@{data.SessionResetsAt:u}, " +
                          $"weekly={data.WeeklyPercent:0.0}% hasWeekly={data.HasWeekly} stale={data.WeeklyStale} " +
                          $"resets@{data.WeeklyResetsAt:u}, " +
                          $"extraEnabled={data.ExtraEnabled} extra=${data.ExtraUsedDollars:F2}/${data.ExtraLimitDollars:F2}";
            if (sig != _lastPollSignature)
            {
                Logger.Info(pollMsg);
                _lastPollSignature = sig;
            }
            else
            {
                Logger.Debug(pollMsg);
            }
            _lastData = data;
            _errors = 0;
            _backoffMs = PollIntervalMs;
            _pollTimer.Interval = PollIntervalMs;

            ScheduleResetPoll(data);
            Updated?.Invoke(data);
        }
        catch (OperationCanceledException) { }
        catch (UnauthorizedAccessException)
        {
            // Reactive refresh: token may have expired between the expiry check and the HTTP call
            Logger.Warn("OAuth 401/403 received — attempting reactive token refresh.");
            var (freshToken, throttled) = await _tokenProvider.ForceRefreshAndGetAsync(_cts.Token);
            if (freshToken != null)
            {
                try
                {
                    var data = await _fetcher.FetchAsync(freshToken, _cts.Token);
                    data = UsageData.MergeCarryForward(_lastData, data);
                    _lastData = data;
                    _errors = 0;
                    _backoffMs = PollIntervalMs;
                    _pollTimer.Interval = PollIntervalMs;
                    ScheduleResetPoll(data);
                    Updated?.Invoke(data);
                    Logger.Info("Reactive refresh succeeded — poll recovered.");
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    Logger.Error("Reactive refresh succeeded but fetch still got 401/403.");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Reactive refresh: fetch after token refresh failed: {ex.Message}");
                }
            }

            _errors++;
            _backoffMs = NextBackoff(_backoffMs);
            _pollTimer.Interval = _backoffMs;

            if (throttled)
            {
                // Throttled: refresh window hasn't elapsed — network may still be settling.
                // Back off silently; the next scheduled poll will try refresh again.
                Logger.Warn("Reactive refresh throttled — backing off silently, will retry.");
            }
            else
            {
                // Refresh was actually attempted and failed — token is genuinely invalid.
                Logger.Error("OAuth token expired or invalid; refresh did not help. Run 'claude login'.");
                AuthExpired?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _errors++;
            _backoffMs = NextBackoff(_backoffMs);
            _pollTimer.Interval = _backoffMs;
            Logger.Error($"Poll failed (attempt {_errors}): {ex.GetType().Name}: {ex.Message}");
            Failed?.Invoke(_lastData, ex.Message, _errors);
        }
        finally
        {
            _pollGuard.Release();
        }
    }

    internal static int NextBackoff(int current) => Math.Min(current * 2, MaxBackoffMs);

    internal static bool ShouldShowStaleIcon(UsageData? last) => last != null;

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
    // POWER / NETWORK RECOVERY
    // ═══════════════════════════════════════

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
            RecoverNow("power resume");
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Only react while we're actually failing, to avoid poll storms on routine changes.
        if (_errors > 0)
            RecoverNow("network address changed");
    }

    // ═══════════════════════════════════════
    // ASYNC HELPER
    // ═══════════════════════════════════════

    private static async void FireAndForget(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Unhandled] {ex}"); }
    }
}
