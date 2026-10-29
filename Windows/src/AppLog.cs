using System.IO;
using System.Reflection;
using System.Text;

namespace LanDrop.Windows;

internal static class AppLog
{
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(AppContext.BaseDirectory, "data", "logs");
    private static readonly string FilePath = Path.Combine(DirectoryPath, $"landrop-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var builder = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                    .Append(" [")
                    .Append(level)
                    .Append("] ")
                    .AppendLine(message);

                if (exception is not null)
                    builder.AppendLine(exception.ToString());

                File.AppendAllText(FilePath, builder.ToString(), Encoding.UTF8);
                TrimOldLogs();
            }
        }
        catch
        {
        }
    }

    private static void TrimOldLogs()
    {
        foreach (var file in new DirectoryInfo(DirectoryPath)
                     .EnumerateFiles("landrop-*.log")
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Skip(14))
        {
            try { file.Delete(); }
            catch { }
        }
    }

    public static void WriteStartup()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Info($"LanDrop started. Version={version}; BaseDirectory={AppContext.BaseDirectory}");
    }
}
