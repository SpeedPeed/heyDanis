using System;
using System.IO;

namespace Clicky.Companion;

/// <summary>
/// Lightweight file logger that appends timestamped lines to
/// <c>%APPDATA%\Clicky\debug.log</c>. Used to capture failures that would
/// otherwise be invisible in a release build (where <see cref="System.Diagnostics.Debug.WriteLine"/>
/// is a no-op without a debugger attached).
///
/// Keep usage minimal — only log things that would help diagnose a user
/// bug report, never per-frame data.
/// </summary>
public static class DebugLog
{
    private static readonly object _lock = new();
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Clicky", "debug.log");

    /// <summary>Absolute path of the log file on disk (for tests and error UX).</summary>
    public static string LogFilePath => _logPath;

    /// <summary>
    /// Appends a timestamped line to the log file. Best-effort: swallows any
    /// IO failures so logging never crashes the pipeline.
    /// </summary>
    public static void Write(string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(
                    _logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort — a broken log must never break the app.
        }
    }

    /// <summary>
    /// Writes an exception with its full ToString() (type, message, stack).
    /// </summary>
    public static void Write(string context, Exception ex)
    {
        Write($"{context}: {ex}");
    }
}
