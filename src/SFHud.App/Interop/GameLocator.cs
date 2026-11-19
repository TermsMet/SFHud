using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SFHud.Services;
using static SFHud.Interop.NativeMethods;

namespace SFHud.Interop;

/// <summary>A running app with a visible main window, offered when adding a custom target.</summary>
public sealed record RunningApp(string ProcessName, string Title);

/// <summary>
/// Finds the window of the selected <see cref="GameTarget"/>. Named targets match process names
/// (plus an optional title hint). The "Any Game" target follows whichever fullscreen or borderless
/// app is in front and remembers it while SFHud itself has focus.
/// </summary>
public static class GameLocator
{
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "TextInputHost", "LockApp", "dwm", "ApplicationFrameHost", "SystemSettings",
    };

    private static readonly Dictionary<uint, (string Name, DateTime At)> PidNames = new();

    private static IntPtr _cached;
    private static string _cachedFor = "";
    private static DateTime _scannedAt = DateTime.MinValue;
    private static IntPtr _lastAuto;

    public static IntPtr Find(GameTarget target)
    {
        if (target.IsAuto) return FindAuto();

        bool fresh = _cachedFor == target.Id && (DateTime.UtcNow - _scannedAt).TotalSeconds < 2;
        if (fresh && (_cached == IntPtr.Zero || IsWindow(_cached))) return _cached;

        _cachedFor = target.Id;
        _scannedAt = DateTime.UtcNow;
        _cached = Scan(target);
        return _cached;
    }

    public static bool IsRunning(GameTarget target) => Find(target) != IntPtr.Zero;

    public static bool IsForeground(GameTarget target)
    {
        IntPtr h = Find(target);
        return h != IntPtr.Zero && GetForegroundWindow() == h;
    }

    /// <summary>Name of the app the auto target is following, for status text.</summary>
    public static string? AutoTargetName()
    {
        if (_lastAuto == IntPtr.Zero) return null;
        GetWindowThreadProcessId(_lastAuto, out uint pid);
        return ProcessName(pid);
    }

    /// <summary>
    /// Walks the visible top-level windows (what Process.MainWindowHandle would pick) instead of taking a
    /// snapshot of every process each time: much cheaper, nothing to dispose, and it can't run the PC out of
    /// memory when it's called every couple of seconds.
    /// </summary>
    private static IntPtr Scan(GameTarget t)
    {
        var names = new HashSet<string>(t.Processes, StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        var title = new System.Text.StringBuilder(256);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (ProcessName(pid) is not { } n || !names.Contains(n)) return true;
            if (t.TitleHint is not null)
            {
                title.Clear();
                GetWindowText(h, title, title.Capacity);
                if (!title.ToString().Contains(t.TitleHint, StringComparison.OrdinalIgnoreCase)) return true;
            }
            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// "Any whitelisted app": follows the foreground window if its process is on the overlay whitelist,
    /// and keeps following it while SFHud's own windows have focus. Browsers, Discord, the desktop and
    /// everything else not on the list never get the overlay.
    /// </summary>
    private static IntPtr FindAuto()
    {
        IntPtr fg = GetForegroundWindow();
        if (IsAllowedWindow(fg)) _lastAuto = fg;
        if (_lastAuto != IntPtr.Zero && (!IsWindow(_lastAuto) || !IsWindowVisible(_lastAuto) || !IsAllowedWindow(_lastAuto, allowIconic: true))) _lastAuto = IntPtr.Zero;
        return _lastAuto;
    }

    /// <summary>True if the window belongs to a process the overlay may appear over (whitelist + selected game).</summary>
    public static bool IsAllowedWindow(IntPtr h, bool allowIconic = false)
    {
        if (h == IntPtr.Zero || !IsWindowVisible(h) || (!allowIconic && IsIconic(h))) return false;
        if (h == GetShellWindow() || h == GetDesktopWindow()) return false;

        GetWindowThreadProcessId(h, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        string? name = ProcessName(pid);
        if (name is null || ShellProcesses.Contains(name)) return false;
        return GameCatalog.Allowed(DataStore.Settings).Contains(name);
    }

    private static string? ProcessName(uint pid)
    {
        // Cached for a minute: Windows reuses process ids, so an old name must not stick forever.
        if (PidNames.TryGetValue(pid, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(1)) return cached.Name;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string n = p.ProcessName;
            if (PidNames.Count > 512) PidNames.Clear();
            PidNames[pid] = (n, DateTime.UtcNow);
            return n;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Bring the target back to the front (after a quick note, or when the editor closes). Windows may refuse.</summary>
    public static bool FocusTarget(GameTarget target)
    {
        IntPtr h = Find(target);
        if (h == IntPtr.Zero) return false;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
        return SetForegroundWindow(h);
    }

    public static List<RunningApp> ListRunningApps()
    {
        var list = new List<RunningApp>();
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id == self || p.MainWindowHandle == IntPtr.Zero) continue;
                    string title = p.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(title) || ShellProcesses.Contains(p.ProcessName)) continue;
                    list.Add(new RunningApp(p.ProcessName, title));
                }
                catch (Exception) { /* access denied / exited */ }
            }
        }
        return list.GroupBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase)
                   .Select(g => g.First())
                   .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }
}
