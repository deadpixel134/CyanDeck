using System.Text;

namespace CyanDeck.Services;

public static class DiagnosticsLog
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(DataRootPaths.ActiveRoaming, "logs");
    private static readonly string CurrentPath = Path.Combine(DirectoryPath, "cyandeck.log");
    private const long MaxBytes = 1_500_000;

    public static string LogFolder => DirectoryPath;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string context, Exception ex) => Write("ERROR", context, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);
                RotateIfNeeded();
                var sb = new StringBuilder();
                sb.Append(DateTimeOffset.Now.ToString("O")).Append(' ').Append(level).Append(' ').Append(message);
                if (ex is not null) sb.AppendLine().Append(ex);
                sb.AppendLine();
                File.AppendAllText(CurrentPath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch { }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(CurrentPath) || new FileInfo(CurrentPath).Length < MaxBytes) return;
            var archive = Path.Combine(DirectoryPath, "cyandeck.previous.log");
            File.Move(CurrentPath, archive, true);
        }
        catch { }
    }
}
