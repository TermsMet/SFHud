using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Win32;

namespace SFHud.Services;

/// <summary>
/// Per-user installer built into SFHud.exe (run as SFHud-Setup.exe or with --install). No admin rights:
/// installs to %LOCALAPPDATA%\Programs\SFHud by default, adds Start Menu / desktop shortcuts and an
/// Apps &amp; Features entry whose Uninstall runs "SFHud.exe --uninstall". User data lives in
/// %LOCALAPPDATA%\SFHud, outside the install folder, so updates and uninstalls never touch it.
/// </summary>
public static class Installer
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SFHud";
    public const string Publisher = "SFHud";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SFHud");
    public static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SFHud.lnk");
    public static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SFHud.lnk");

    public sealed record Existing(string Dir, string Version, string Edition);

    /// <summary>"Free" or "Pro": the edition this exe was built as.</summary>
    public static string ThisEdition => Services.Edition.IsProBuild ? "Pro" : "Free";

    /// <summary>Program files older or other-edition builds may have left in the install folder (a folder
    /// build, a half-finished update). Only these exact names are removed, never anything else in the folder,
    /// and never the data folder (%LOCALAPPDATA%\SFHud), which is separate.</summary>
    private static readonly string[] StaleProgramFiles =
    {
        "SFHud.exe.new", "SFHud.dll", "SFHud.pdb", "SFHud.deps.json", "SFHud.runtimeconfig.json",
        "PresentationNative_cor3.dll", "wpfgfx_cor3.dll", "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "vcruntime140_cor3.dll",
    };

    public static Existing? FindExisting()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
            if (k?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, "SFHud.exe")))
                return new Existing(dir, k.GetValue("DisplayVersion") as string ?? "?", k.GetValue("Edition") as string ?? "Free");
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>Asks a running SFHud to close (it saves first) and waits for it.</summary>
    public static bool CloseRunning(int timeoutMs = 8000)
    {
        try
        {
            if (!Mutex.TryOpenExisting(App.MutexName, out var m)) return true;
            m.Dispose();
            try { using var ev = EventWaitHandle.OpenExisting(App.ExitEventName); ev.Set(); } catch (Exception) { }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(250);
                if (!Mutex.TryOpenExisting(App.MutexName, out var still)) return WaitForExit(Path.Combine(DefaultDir, "SFHud.exe"), 3000);
                still.Dispose();
            }
            return false;
        }
        catch (Exception) { return true; }
    }

    private static bool WaitForExit(string _, int ms)
    {
        int self = Environment.ProcessId;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (!Process.GetProcessesByName("SFHud").Any(p => p.Id != self)) return true;
            Thread.Sleep(200);
        }
        return true;
    }

    public static string Install(string dir, bool desktop, bool startMenu, Action<string> progress)
    {
        string src = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the setup file.");
        dir = Path.GetFullPath(dir);
        progress("Creating folder");
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "SFHud.exe");

        progress("Closing running SFHud");
        CloseRunning();
        if (RunningFrom(dir)) throw new InvalidOperationException("SFHud is still running from the install folder. Close it (it saves first) and try again.");

        if (!string.Equals(Path.GetFullPath(src), exe, StringComparison.OrdinalIgnoreCase))
        {
            progress("Copying SFHud.exe");
            string tmp = exe + ".new";
            File.Copy(src, tmp, overwrite: true);
            for (int i = 0; ; i++)
            {
                try { File.Move(tmp, exe, overwrite: true); break; }
                catch (IOException) when (i < 20) { Thread.Sleep(250); } // exe still locked for a moment after exit
            }
        }

        progress("Removing old program files");
        foreach (var f in StaleProgramFiles) TryDelete(Path.Combine(dir, f));

        progress("Removing the old SHud install");
        RemoveRenamedInstall();

        progress("Writing README and changelog");
        WriteResource("SFHud.README.md", Path.Combine(dir, "README.md"));
        WriteResource("SFHud.CHANGELOG.md", Path.Combine(dir, "CHANGELOG.md"));

        progress("Creating shortcuts");
        if (startMenu) CreateShortcut(StartMenuLink, exe, dir);
        else TryDelete(StartMenuLink);
        if (desktop) CreateShortcut(DesktopLink, exe, dir);
        else TryDelete(DesktopLink);

        progress("Registering with Windows");
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", Services.Edition.IsProBuild ? "SFHud Pro" : "SFHud");
            k.SetValue("Edition", ThisEdition);
            k.SetValue("DisplayVersion", AppInfo.Version);
            k.SetValue("Publisher", Publisher);
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("InstallLocation", dir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("URLInfoAbout", AppInfo.DonateUrl);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            long kb = new FileInfo(exe).Length / 1024;
            k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
        }
        progress("Done");
        return exe;
    }

    public static void Uninstall(bool deleteData, bool deleteBackups)
    {
        string dir = AppContext.BaseDirectory.TrimEnd('\\');
        var existing = FindExisting();
        if (existing is not null) dir = existing.Dir;

        CloseRunningOthers(dir);
        TryDelete(StartMenuLink);
        TryDelete(DesktopLink);
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch (Exception) { }
        TryDelete(Path.Combine(dir, "README.md"));
        TryDelete(Path.Combine(dir, "CHANGELOG.md"));

        if (deleteData && Directory.Exists(SettingsStore.DataDir))
        {
            foreach (var d in Directory.GetDirectories(SettingsStore.DataDir))
                if (deleteBackups || !string.Equals(Path.GetFileName(d), "backups", StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(d, recursive: true); } catch (Exception) { }
            foreach (var f in Directory.GetFiles(SettingsStore.DataDir))
            {
                if (!deleteBackups && Path.GetFileName(f) == "userdata.json")
                {
                    Directory.CreateDirectory(SettingsStore.BackupDir);
                    try { File.Copy(f, Path.Combine(SettingsStore.BackupDir, $"userdata-{DateTime.Now:yyyyMMdd-HHmmss}-uninstall.json"), true); } catch (Exception) { }
                }
                TryDelete(f);
            }
            if (deleteBackups) try { Directory.Delete(SettingsStore.DataDir, recursive: true); } catch (Exception) { }
        }

        // The running exe can't delete itself: a short-lived hidden cmd removes it (and the folder, only if empty) after we exit.
        string exe = Path.Combine(dir, "SFHud.exe");
        string cmd = $"/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{exe}\" & rmdir \"{dir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }

    /// <summary>
    /// 1.0 was installed as "SHud": its shortcuts, Apps &amp; Features entry and exe are replaced by SFHud's.
    /// Its data folder is left for SFHud to bring over on first start.
    /// </summary>
    private static void RemoveRenamedInstall()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SHud";
        try
        {
            App.CloseLegacyCopy();
            string? dir;
            using (var k = Registry.CurrentUser.OpenSubKey(key)) dir = k?.GetValue("InstallLocation") as string;
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SHud.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SHud.lnk"));
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            if (dir is { Length: > 0 } && Directory.Exists(dir))
            {
                foreach (var f in new[] { "SHud.exe", "README.md", "CHANGELOG.md" }) TryDelete(Path.Combine(dir, f));
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch (Exception) { }
            }
        }
        catch (Exception ex) { Log.Warn("remove old SHud install: " + ex.Message); }
    }

    private static bool RunningFrom(string dir)
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("SFHud").Where(p => p.Id != self))
            try { if (p.MainModule?.FileName is { } f && f.StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true; } catch (Exception) { }
        return false;
    }

    /// <summary>Asks SFHud to close, then ends only copies running from the install folder (never other copies elsewhere).</summary>
    private static void CloseRunningOthers(string dir)
    {
        CloseRunning(5000);
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName("SFHud").Where(p => p.Id != self))
        {
            try
            {
                string? path = p.MainModule?.FileName;
                if (path is null || !path.StartsWith(dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (!p.WaitForExit(3000)) p.Kill();
            }
            catch (Exception) { }
        }
    }

    private static void CreateShortcut(string link, string target, string workDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic sc = shell.CreateShortcut(link);
            sc.TargetPath = target;
            sc.WorkingDirectory = workDir;
            sc.IconLocation = target + ",0";
            sc.Description = "SFHud - game overlay and note pads for Garry's Mod RP";
            sc.Save();
        }
        catch (Exception ex) { Log.Warn("shortcut: " + ex.Message); }
    }

    private static void WriteResource(string name, string path)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s is null) return;
            using var f = File.Create(path);
            s.CopyTo(f);
        }
        catch (Exception ex) { Log.Warn("resource " + name + ": " + ex.Message); }
    }

    public static string ReadResource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s is null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}
