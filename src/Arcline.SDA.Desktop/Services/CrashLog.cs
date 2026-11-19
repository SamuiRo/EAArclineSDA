using System.Text;

namespace Arcline.SDA.Desktop.Services;

/// <summary>
/// Appends unexpected errors to a daily log next to the vault's parent folder:
/// %APPDATA%\ArclineSDA\logs on Windows, ~/.config/ArclineSDA/logs on Linux.
/// Only exception types, messages and stack traces are written; never secrets or tokens.
/// </summary>
public static class CrashLog
{
    private static readonly Lock Sync = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create), "ArclineSDA", "logs");

    public static string CurrentFile => Path.Combine(Directory, $"arcline-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Write(string source, Exception ex)
    {
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var sb = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("O")).Append(" [").Append(source).Append("] ")
                    .Append(typeof(CrashLog).Assembly.GetName().Version).AppendLine()
                    .AppendLine(ex.ToString())
                    .AppendLine();
                File.AppendAllText(CurrentFile, sb.ToString());
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
