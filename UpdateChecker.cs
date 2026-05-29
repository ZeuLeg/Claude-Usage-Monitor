using System.Reflection;
using System.Text.Json;

namespace ClaudeUsageMonitor;

internal static class UpdateChecker
{
    private const string Repo        = "ZeuLeg/Claude-Usage-Monitor";
    private const string ApiUrl      = $"https://api.github.com/repos/{Repo}/releases/latest";
    public  const string ReleasesUrl = $"https://github.com/{Repo}/releases";

    private const string LastCheckFile = "last_update_check.txt";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static UpdateChecker()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ClaudeCodeInfo.UserAgent);
    }

    /// <summary>
    /// Returns the latest release tag (e.g. "0.6.0") if it is newer than the running
    /// assembly version, or null if already up to date or the check fails.
    /// </summary>
    public static async Task<string?> CheckAsync()
    {
        try
        {
            var json   = await _http.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);
            var tag    = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v');
            if (tag == null) return null;

            var current = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            return IsNewer(tag, current) ? tag : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"Update check failed: {ex.Message}");
            return null;
        }
    }

    public static bool ShouldCheckToday()
    {
        var path = Path.Combine(Logger.LogDirectory, LastCheckFile);
        try
        {
            if (File.Exists(path) &&
                DateTime.TryParse(File.ReadAllText(path).Trim(), out var last))
                return DateTime.Today > last.Date;
        }
        catch { }
        return true;
    }

    public static void RecordCheckTime()
    {
        try
        {
            Directory.CreateDirectory(Logger.LogDirectory);
            File.WriteAllText(
                Path.Combine(Logger.LogDirectory, LastCheckFile),
                DateTime.Today.ToString("yyyy-MM-dd"));
        }
        catch { }
    }

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest,  out var l) &&
            Version.TryParse(current, out var c))
            return l > c;
        return false;
    }
}
