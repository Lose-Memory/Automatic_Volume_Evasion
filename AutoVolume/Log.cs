using System.IO;
using System.Text;

namespace AutoVolume;

public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                _path ??= Path.Combine(AppContext.BaseDirectory, "log.txt");
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                    File.Move(_path, _path + ".old", overwrite: true);

                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}