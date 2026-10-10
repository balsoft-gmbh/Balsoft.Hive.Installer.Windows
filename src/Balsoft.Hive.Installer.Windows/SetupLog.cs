using System;
using System.IO;
using System.Text;

namespace Balsoft.Hive.Installer.Windows;

/// <summary>
/// The Setup log. Every step is written as it happens (flushed per line), so a log read while
/// Setup still runs, or after it was killed, shows how far it got. An application that started an
/// update can read the last lines to show why it did not complete. Passwords are never written.
/// </summary>
internal sealed class SetupLog : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _gate = new();

    public string? Path { get; }

    /// <summary>Raised for every line, for the progress view.</summary>
    public event Action<string>? Line;

    public SetupLog(string? path)
    {
        Path = path;
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            _writer = null;   // a log that cannot be written must not stop the install
            Path = null;
        }
    }

    /// <summary>%TEMP%\Setup Log yyyy-MM-dd #nnn.txt, the log file for /LOG without a path.</summary>
    public static string DefaultPath()
    {
        string temp = System.IO.Path.GetTempPath();
        string day = DateTime.Now.ToString("yyyy-MM-dd");
        for (int i = 1; i < 1000; i++)
        {
            string p = System.IO.Path.Combine(temp, $"Setup Log {day} #{i:000}.txt");
            if (!File.Exists(p)) return p;
        }
        return System.IO.Path.Combine(temp, $"Setup Log {day} #{Guid.NewGuid():N}.txt");
    }

    public void Info(string message) => Write("INF", message);
    public void Warn(string message) => Write("WRN", message);
    public void Error(string message) => Write("ERR", message);

    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (_gate)
        {
            try { _writer?.WriteLine(line); } catch { /* logging never stops the install */ }
        }
        Line?.Invoke(line);
    }

    public void Dispose()
    {
        lock (_gate) { _writer?.Dispose(); }
    }
}
