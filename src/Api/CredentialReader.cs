using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageMonitor;

/// <summary>
/// Reads the OAuth Access Token used to call the usage API.
///
/// Source order:
///   0. CLAUDE_CODE_OAUTH_TOKEN env var — a long-lived token from `claude setup-token`
///      (no daily expiry; the officially supported headless path)
///   1. Windows Credential Manager: "Claude Code-credentials"
///   2. %USERPROFILE%\.claude\.credentials.json (+ HOMEDRIVE/HOMEPATH fallbacks)
/// </summary>
public static class CredentialReader
{
    private const string CredentialName = "Claude Code-credentials";
    private const string FileName = ".credentials.json";
    private const string DirName = ".claude";

    // The token source is logged only when it changes, to keep the 2-minute poll log quiet.
    private static string? _lastLoggedSource;

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

    /// <summary>Reads the OAuth Access Token, or null if none can be found.</summary>
    public static string? GetAccessToken()
    {
        // 0. Explicit long-lived token (claude setup-token) — official, survives daily expiry.
        var envToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
        {
            LogSource("CLAUDE_CODE_OAUTH_TOKEN env var");
            return envToken.Trim();
        }

        // 1. Windows Credential Manager
        try
        {
            var json = ReadFromCredentialManager();
            var token = json != null ? ExtractAccessToken(json) : null;
            if (token != null) { LogSource("Credential Manager"); return token; }
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

                var token = ExtractAccessToken(json);
                if (token != null) { LogSource($"file: {path}"); return token; }
            }
            catch (Exception ex)
            {
                Logger.Warn($"[CredentialReader] File error ({path}): {ex.Message}");
            }
        }

        LogSource("none");
        return null;
    }

    /// <summary>Logs the token source on change only, so steady-state polling stays silent.</summary>
    private static void LogSource(string source)
    {
        if (source == _lastLoggedSource) return;
        _lastLoggedSource = source;
        if (source == "none")
            Logger.Warn("[CredentialReader] No token found in any source (run 'claude login')");
        else
            Logger.Info($"[CredentialReader] Token source: {source}");
    }

    /// <summary>All candidate paths for the credentials file.</summary>
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

    private static string? ExtractAccessToken(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.TryGetProperty("accessToken", out var token))
            {
                var val = token.GetString();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[CredentialReader] JSON parse error: {ex.Message}");
        }
        return null;
    }
}
