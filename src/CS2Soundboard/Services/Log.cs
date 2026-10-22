using System;
using System.IO;

namespace CS2Soundboard.Services;

/// <summary>Minimal file logger (never called from real-time audio callbacks).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static string? FilePath => _path;

    public static void Init(string directory)
    {
        try
        {
            var dir = Path.Combine(directory, "logs");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "soundboard.log");
            var fi = new FileInfo(_path);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                File.Copy(_path, Path.Combine(dir, "soundboard.old.log"), true);
                File.Delete(_path);
            }
        }
        catch
        {
            _path = null;
        }
    }

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (_path == null) return;
        try
        {
            lock (Gate)
            {
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{(ex != null ? Environment.NewLine + ex : "")}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
