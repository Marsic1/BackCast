using System.Text;

namespace Backcast;

/// <summary>
/// Append-only diagnostic log (mirrors WebStage's Ui::Log pattern):
/// %TEMP%\Backcast.log. Kept minimal — enough to debug the player/watchdog.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Backcast.log");

    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
