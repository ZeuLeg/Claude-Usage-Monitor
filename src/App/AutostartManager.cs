namespace ClaudeUsageMonitor;

/// <summary>Toggles "start with Windows" via the per-user HKCU Run key.</summary>
internal static class AutostartManager
{
    private const string Name   = "ClaudeUsageMonitor";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(Name) != null;
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe != null) key.SetValue(Name, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(Name, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Autostart toggle failed: {ex.Message}");
        }
    }
}
