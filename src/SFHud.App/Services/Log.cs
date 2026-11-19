using System;
using System.IO;
using System.Linq;

namespace SFHud.Services;

/// <summary>
/// Small diagnostic log in %LOCALAPPDATA%\SFHud\logs (one file per day, last 7 kept).
/// Never throws: logging must not be the thing that breaks the app.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static bool _pruned;

    public static string CurrentPath => Path.Combine(SettingsStore.LogDir, $"sfhud-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.LogDir);
                File.AppendAllText(CurrentPath, $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {msg}{Environment.NewLine}");
                if (!_pruned)
                {
                    _pruned = true;
                    foreach (var old in new DirectoryInfo(SettingsStore.LogDir).GetFiles("sfhud-*.log").OrderByDescending(f => f.Name).Skip(7))
                        old.Delete();
                }
            }
        }
        catch (Exception) { /* ignore */ }
    }
}
