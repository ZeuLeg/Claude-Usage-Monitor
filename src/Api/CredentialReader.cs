using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>
/// Reads the OAuth credentials used to call the usage API.
///
/// Source order (highest priority first):
///   1. Windows Credential Manager: "Claude Code-credentials"
///   2. %USERPROFILE%\.claude\.credentials.json (+ HOMEDRIVE/HOMEPATH fallbacks)
///   3. CLAUDE_CODE_OAUTH_TOKEN env var (last resort — likely lacks user:profile scope)
/// </summary>
public static class CredentialReader
{
    private const string CredentialName = "Claude Code-credentials";
    private const string FileName = ".credentials.json";
    private const string DirName = ".claude";

    private static string? _lastLoggedSource;
    private static bool _envVarWarned;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    public record Credentials(string AccessToken, DateTime ExpiresAt,
                               string? SubscriptionType = null,
                               string? RateLimitTier   = null);

    /// <summary>
    /// Returns the OAuth credentials (token + expiry), or null if none can be found.
    /// Reads from Credential Manager → credentials file → env var (last resort).
    /// </summary>
    public static Credentials? ReadCredentials()
    {
        // 1. Windows Credential Manager
        try
        {
            var json = ReadFromCredentialManager();
            var creds = json != null ? ExtractCredentials(json) : null;
            if (creds != null) { LogSource("Credential Manager"); return creds; }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CredentialReader] Credential Manager error: {ex.Message}");
        }

        // 2. Credentials file (multiple candidate paths)
        foreach (var path in GetCredentialFilePaths())
        {
            try
            {
                if (!File.Exists(path)) continue;

                var json = File.ReadAllText(path, Encoding.UTF8);
                if (!json.Contains("claudeAiOauth")) continue;

                var creds = ExtractCredentials(json);
                if (creds != null) { LogSource($"file: {path}"); return creds; }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[CredentialReader] File error ({path}): {ex.Message}");
            }
        }

        // 3. Env var — last resort; likely lacks user:profile scope required by usage endpoint
        var envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            if (!_envVarWarned)
            {
                _envVarWarned = true;
                Logger.Warn("[CredentialReader] Using CLAUDE_CODE_OAUTH_TOKEN env var — this token likely lacks user:profile scope and may return HTTP 403. Remove it and use 'claude login' instead.");
            }
            LogSource("CLAUDE_CODE_OAUTH_TOKEN env var");
            // Env var has no expiry info; treat as already expired so refresh is attempted
            return new Credentials(envToken.Trim(), DateTime.MinValue);
        }

        LogSource("none");
        return null;
    }

    /// <summary>Reads the OAuth Access Token, or null if none can be found.</summary>
    public static string? GetAccessToken() => ReadCredentials()?.AccessToken;

    private static void LogSource(string source)
    {
        if (source == _lastLoggedSource) return;
        _lastLoggedSource = source;
        if (source == "none")
            Logger.Warn("[CredentialReader] No token found in any source (run 'claude login')");
        else
            Logger.Info($"[CredentialReader] Token source: {source}");
    }

    private static IEnumerable<string> GetCredentialFilePaths()
    {
        var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(userProfile))
            yield return Path.Combine(userProfile, DirName, FileName);

        var specialFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(specialFolder) && specialFolder != userProfile)
            yield return Path.Combine(specialFolder, DirName, FileName);

        var homeDrive = Environment.GetEnvironmentVariable("HOMEDRIVE");
        var homePath = Environment.GetEnvironmentVariable("HOMEPATH");
        if (!string.IsNullOrEmpty(homeDrive) && !string.IsNullOrEmpty(homePath))
        {
            var combined = homeDrive + homePath;
            if (combined != userProfile && combined != specialFolder)
                yield return Path.Combine(combined, DirName, FileName);
        }
    }

    private static string? ReadFromCredentialManager()
    {
        if (!CredReadW(CredentialName, 1, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            if (cred.CredentialBlobSize <= 0) return null;

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);

            var text = Encoding.UTF8.GetString(bytes);
            if (text.Contains("claudeAiOauth")) return text;

            // UTF-16 fallback
            text = Encoding.Unicode.GetString(bytes);
            return text.Contains("claudeAiOauth") ? text : null;
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    private static Credentials? ExtractCredentials(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
                return null;

            if (!oauth.TryGetProperty("accessToken", out var tokenProp))
                return null;

            // Guard against explicit JSON null: { "accessToken": null }
            if (tokenProp.ValueKind != JsonValueKind.String)
                return null;

            var token = tokenProp.GetString();
            if (string.IsNullOrWhiteSpace(token)) return null;

            var expiresAt = DateTime.MaxValue;
            if (oauth.TryGetProperty("expiresAt", out var expProp) &&
                expProp.ValueKind == JsonValueKind.Number &&
                expProp.TryGetInt64(out var epochMs))
            {
                expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
            }

            string? subscriptionType = null;
            if (oauth.TryGetProperty("subscriptionType", out var stProp) &&
                stProp.ValueKind == JsonValueKind.String)
                subscriptionType = stProp.GetString();

            string? rateLimitTier = null;
            if (oauth.TryGetProperty("rateLimitTier", out var rltProp) &&
                rltProp.ValueKind == JsonValueKind.String)
                rateLimitTier = rltProp.GetString();

            return new Credentials(token, expiresAt, subscriptionType, rateLimitTier);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CredentialReader] JSON parse error: {ex.Message}");
            return null;
        }
    }
}
