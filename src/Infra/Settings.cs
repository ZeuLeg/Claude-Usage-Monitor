using System.Text.Json;

namespace ClaudeUsageMonitor;

internal class Settings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeUsageMonitor", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static Settings? _current;
    public static Settings Current => _current ??= CreateFromDisk();

    public string NtfyServer { get; set; } = "https://ntfy.sh";
    public string NtfyTopic { get; set; } = "";
    public string ShellCommand { get; set; } = "";
    public bool NotifyLimitReached { get; set; } = true;
    public bool NotifyReset { get; set; } = true;
    public bool NotifyHighUsage { get; set; } = true;

    public static void Load()
    {
        _current = CreateFromDisk();
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
        catch { }
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
