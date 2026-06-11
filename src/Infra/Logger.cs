namespace ClaudeUsageMonitor;

public enum LogLevel { Error = 0, Warn = 1, Info = 2, Debug = 3 }

internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeUsageMonitor", "log.txt");

    private const long MaxBytes = 1_048_576; // 1 MB before rotation

    private static readonly object _lock = new();

    public static string LogDirectory => Path.GetDirectoryName(LogPath)!;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static void Error(string msg) => Write(LogLevel.Error, "ERROR", msg);
    public static void Warn(string msg)  => Write(LogLevel.Warn,  "WARN ", msg);
    public static void Info(string msg)  => Write(LogLevel.Info,  "INFO ", msg);
    public static void Debug(string msg) => Write(LogLevel.Debug, "DEBUG", msg);

    private static void Write(LogLevel level, string tag, string msg)
    {
        if (level > MinLevel) return;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (_lock)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var bak = Path.Combine(LogDirectory, "log.old.txt");
                    File.Move(LogPath, bak, overwrite: true);
                }
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{tag}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* never crash the app over logging */ }
    }
}
