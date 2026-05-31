namespace ClaudeUsageMonitor;

/// <summary>
/// Orchestrates token reads, expiry checks, and proactive/reactive refresh.
/// Call GetValidAccessTokenAsync() before each fetch; call ForceRefreshAndGetAsync() on 401/403.
/// </summary>
internal sealed class TokenProvider
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly TokenRefresher _refresher = new();

    /// <summary>
    /// Returns a valid access token, refreshing via the CLI if the stored token is expired.
    /// Returns null if no credentials exist or refresh fails.
    /// </summary>
    public async Task<(string? token, bool hasCredentials)> GetValidAccessTokenAsync(CancellationToken ct = default)
    {
        var creds = CredentialReader.ReadCredentials();
        if (creds == null) return (null, false);

        if (creds.ExpiresAt - ExpirySkew > DateTime.UtcNow)
            return (creds.AccessToken, true);

        // Token expired (or env var fallback with DateTime.MinValue) — try refresh
        Logger.Info($"[TokenProvider] Token expires at {creds.ExpiresAt:u}, attempting refresh.");
        var refreshed = await _refresher.TryRefreshAsync(ct);
        if (refreshed)
        {
            var fresh = CredentialReader.ReadCredentials();
            if (fresh != null && fresh.ExpiresAt - ExpirySkew > DateTime.UtcNow)
                return (fresh.AccessToken, true);
        }

        // Refresh failed but we still have a token — try it anyway (may still work)
        Logger.Warn("[TokenProvider] Refresh did not produce a fresh token; using existing token.");
        return (creds.AccessToken, true);
    }

    /// <summary>
    /// Called reactively after a 401/403. Forces a refresh attempt and returns the new token,
    /// or null if refresh fails. Skips the throttle check — this is an emergency path.
    /// </summary>
    public async Task<string?> ForceRefreshAndGetAsync(CancellationToken ct = default)
    {
        Logger.Info("[TokenProvider] Force refresh triggered by 401/403.");
        var refreshed = await _refresher.TryRefreshAsync(ct);
        if (!refreshed)
        {
            Logger.Warn("[TokenProvider] Force refresh failed.");
            return null;
        }

        var creds = CredentialReader.ReadCredentials();
        if (creds == null || creds.ExpiresAt - ExpirySkew <= DateTime.UtcNow)
        {
            Logger.Warn("[TokenProvider] Force refresh: still no valid token after refresh.");
            return null;
        }

        Logger.Info("[TokenProvider] Force refresh succeeded.");
        return creds.AccessToken;
    }
}
