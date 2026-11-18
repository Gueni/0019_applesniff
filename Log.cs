using System.IO;

namespace Sniffer;

/// <summary>Plain-text diagnostics in %AppData%\Sniffer\log.txt, kept small.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string FilePath => _path ??= Path.Combine(Settings.AppDataDir, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public static void Error(string what, Exception ex) => Write($"{what}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
