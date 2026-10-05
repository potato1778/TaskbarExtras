using System.IO;

namespace TaskbarExtras.App;

/// <summary>
/// Dead-simple file log. Deliberately not a logging framework — this exists so a user can
/// send us one file when the taskbar-menu interception misbehaves, and so the interception
/// logic is observable at all. See docs/DESIGN.md §6.4 B2′.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TaskbarExtras.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    public static void Reset()
    {
        try
        {
            lock (Gate)
            {
                File.WriteAllText(Path, $"TaskbarExtras log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}");
            }
        }
        catch
        {
            // ignore
        }
    }
}
