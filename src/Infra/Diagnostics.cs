using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeUsageMonitor;

internal static class Diagnostics
{
    public static string BuildReport()
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = version is null ? "?" : $"{version.Major}.{version.Minor}.{version.Build}";

        sb.AppendLine(ic, $"Claude Usage Monitor v{verStr}");
        sb.AppendLine(ic, $"OS: {RuntimeInformation.OSDescription}");
        sb.AppendLine(ic, $"Runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine();

        // Settings summary — redact sensitive fields
        var s = Settings.Current;
        sb.AppendLine("[Settings]");
        sb.AppendLine(ic, $"NtfyServer: {s.NtfyServer}");
        sb.AppendLine(ic, $"NtfyTopic: {(string.IsNullOrEmpty(s.NtfyTopic) ? "(not set)" : "***")}");
        sb.AppendLine(ic, $"NotifyHighUsage: {s.NotifyHighUsage}");
        sb.AppendLine(ic, $"NotifyLimitReached: {s.NotifyLimitReached}");
        sb.AppendLine(ic, $"NotifyReset: {s.NotifyReset}");
        sb.AppendLine(ic, $"LogLevel: {s.LogLevel}");
        sb.AppendLine();

        // Last ~50 lines of log
        sb.AppendLine("[Last log lines]");
        try
        {
            var logPath = Path.Combine(Logger.LogDirectory, "log.txt");
            if (File.Exists(logPath))
            {
                var lines = File.ReadAllLines(logPath);
                var start = Math.Max(0, lines.Length - 50);
                foreach (var line in lines.AsSpan(start))
                    sb.AppendLine(line);
            }
            else
            {
                sb.AppendLine("(log file not found)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine(ic, $"(could not read log: {ex.Message})");
        }

        return sb.ToString();
    }
}
