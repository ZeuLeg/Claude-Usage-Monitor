using System.Diagnostics;

namespace ClaudeUsageMonitor;

/// <summary>
/// Delegates token refresh to the official Claude CLI so we never touch credentials directly.
/// Tries `claude auth status` first (lightweight), then `claude update` if needed.
/// Throttled to at most one attempt per 5 minutes.
/// </summary>
internal sealed class TokenRefresher
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastAttempt = DateTime.MinValue;

    /// <summary>
    /// Attempts to refresh the OAuth token via the Claude CLI.
    /// Returns true if a valid token is now available (expiresAt advanced into the future).
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct)) return false;
        try
        {
            var now = DateTime.UtcNow;
            if (now - _lastAttempt < MinInterval)
            {
                Logger.Info("[TokenRefresher] Throttled — last attempt was too recent.");
                return false;
            }
            _lastAttempt = now;

            var before = CredentialReader.ReadCredentials()?.ExpiresAt ?? DateTime.MinValue;

            // First try: lightweight status check — may silently refresh the token
            Logger.Info("[TokenRefresher] Trying 'claude auth status'...");
            await RunClaudeAsync("auth status", timeoutMs: 15_000, ct);

            var after = CredentialReader.ReadCredentials()?.ExpiresAt ?? DateTime.MinValue;
            if (after > before && after > DateTime.UtcNow.AddSeconds(30))
            {
                Logger.Info($"[TokenRefresher] Token refreshed via 'claude auth status'. New expiresAt: {after:u}");
                return true;
            }

            // Second try: claude update (validated approach from jens-duttke/usage-monitor-for-claude)
            Logger.Info("[TokenRefresher] Trying 'claude update'...");
            var updateOutput = await RunClaudeAsync("update", timeoutMs: 60_000, ct);
            if (!string.IsNullOrWhiteSpace(updateOutput))
                Logger.Info($"[TokenRefresher] 'claude update' output: {updateOutput.Trim()}");

            after = CredentialReader.ReadCredentials()?.ExpiresAt ?? DateTime.MinValue;
            if (after > before && after > DateTime.UtcNow.AddSeconds(30))
            {
                Logger.Info($"[TokenRefresher] Token refreshed via 'claude update'. New expiresAt: {after:u}");
                return true;
            }

            Logger.Warn("[TokenRefresher] Neither 'claude auth status' nor 'claude update' advanced expiresAt.");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> RunClaudeAsync(string arguments, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("claude", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)!;
            var readTask = proc.StandardOutput.ReadToEndAsync(ct);
            if (!proc.WaitForExit(timeoutMs))
            {
                try { proc.Kill(); } catch { }
                Logger.Warn($"[TokenRefresher] 'claude {arguments}' timed out after {timeoutMs / 1000}s.");
                return string.Empty;
            }
            return await readTask;
        }
        catch (Exception ex)
        {
            Logger.Warn($"[TokenRefresher] 'claude {arguments}' failed: {ex.Message}");
            return string.Empty;
        }
    }
}
