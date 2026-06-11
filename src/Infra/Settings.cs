using System.Text.Json;

namespace ClaudeUsageMonitor;

internal class Settings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeUsageMonitor", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly object _lock = new();
    private static Settings? _current;

    public static Settings Current
    {
        get
        {
            lock (_lock) { return _current ??= CreateFromDisk(); }
        }
    }

    public string LogLevel { get; set; } = "Info";
    public string NtfyServer { get; set; } = "https://ntfy.sh";
    public string NtfyTopic { get; set; } = "";
    public bool NotifyLimitReached { get; set; } = true;
    public bool NotifyReset { get; set; } = true;
    public bool NotifyHighUsage { get; set; } = true;
    public bool NotifyDepletion { get; set; } = true;
    public int HighUsageThreshold { get; set; } = 90;
    /// <summary>
    /// Minutes between repeated HighUsage notifications while usage stays above the threshold.
    /// 0 = fire only once per window (legacy behavior).
    /// </summary>
    public int HighUsageCooldownMinutes { get; set; } = 0;

    public static void Load()
    {
        lock (_lock) { _current = CreateFromDisk(); }
    }

    private static Settings CreateFromDisk()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Settings] Failed to load {SettingsPath}: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }
}
