namespace ClaudeUsageMonitor;

internal static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeUsageMonitor", "log.txt");

    private const long MaxBytes = 1_048_576; // 1 MB before rotation

    private static readonly object _lock = new();

    public static string LogDirectory => Path.GetDirectoryName(LogPath)!;

    public static void Info(string msg)  => Write("INFO ", msg);
    public static void Warn(string msg)  => Write("WARN ", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string msg)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (_lock)
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var bak = LogPath + ".bak";
                    if (File.Exists(bak)) File.Delete(bak);
                    File.Move(LogPath, bak);
                }
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* never crash the app over logging */ }
    }
}
