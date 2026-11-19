using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SFHud.Services;
using SFHud.Views;

namespace SFHud;

public partial class App : Application
{
    // A copy with its own SFHUD_DATA_DIR (portable / test build) gets its own instance lock.
    private static readonly string Suffix = SettingsStore.CustomDataDir is { Length: > 0 } d
        ? "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(d.ToLowerInvariant())))[..10]
        : "";
    public static readonly string MutexName = @"Local\SFHud.SingleInstance" + Suffix;
    public static readonly string ShowEventName = @"Local\SFHud.Show" + Suffix;
    public static readonly string ExitEventName = @"Local\SFHud.Exit" + Suffix;

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent, _exitEvent;
    private int _crashes;
    private DateTime _firstCrash;

    /// <summary>True when the user or Windows asks for reduced motion. Controls skip their animations.</summary>
    public static bool ReducedMotion { get; set; }

    public static AppSettings Settings => DataStore.Settings;
    public static KeybindHost? Keys { get; set; }

    /// <summary>SFHUD_SCREENSHOT=1: overlay previews on the desktop and ignores focus (for testing / documentation shots).</summary>
    /// Debug builds only: an installed (Release) SFHud can never enter it, so it always shows your real music.
#if DEBUG
    public static bool ScreenshotMode { get; } = Environment.GetEnvironmentVariable("SFHUD_SCREENSHOT") == "1" || Environment.GetEnvironmentVariable("SHUD_SCREENSHOT") == "1";
#else
    public static bool ScreenshotMode => false;
#endif

    // The same app before the rename to SFHud (1.0 shipped as SHud.exe).
    private const string LegacyMutexName = @"Local\SHud.SingleInstance";
    private const string LegacyExitEventName = @"Local\SHud.Exit";

    /// <summary>Asks a running 1.0 "SHud" (it saves first) to close so the two don't fight over hotkeys and data.</summary>
    public static void CloseLegacyCopy(int timeoutMs = 6000)
    {
        try
        {
            if (!Mutex.TryOpenExisting(LegacyMutexName, out var m)) return;
            m.Dispose();
            try { using var ev = EventWaitHandle.OpenExisting(LegacyExitEventName); ev.Set(); } catch (Exception) { return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                Thread.Sleep(200);
                if (!Mutex.TryOpenExisting(LegacyMutexName, out var still)) { Thread.Sleep(400); return; }
                still.Dispose();
            }
        }
        catch (Exception) { }
    }

    /// <summary>Current key combo for an action ("" if unbound), for hints in the UI.</summary>
    public static string Gesture(string action) => Keys?.GestureFor(action) ?? "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "SFHud");
        bool uninstall = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
        bool install = e.Args.Any(a => a.Equals("--install", StringComparison.OrdinalIgnoreCase)) || exeName.Contains("setup", StringComparison.OrdinalIgnoreCase);
        if (install || uninstall)
        {
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            var w = new InstallerWindow(uninstall);
            MainWindow = w;
            w.Show();
            return;
        }

        _singleInstance = new Mutex(true, MutexName, out bool isFirst);
        if (!isFirst)
        {
            // Already running: bring that copy's window forward instead of starting a second one.
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch (Exception) { }
            Shutdown();
            return;
        }

        // First run after the rename: close an old SHud that's still open, then bring its data over.
        string? migrated = null;
        if (SettingsStore.CustomDataDir is null && !File.Exists(DataStore.DataPath))
        {
            CloseLegacyCopy();
            migrated = SettingsStore.MigrateRenamedDataDir();
        }

        DispatcherUnhandledException += OnUiCrash;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { Log.Error("fatal", ex.ExceptionObject as Exception); TrySave(); };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("task", ex.Exception); ex.SetObserved(); };
        SessionEnding += (_, _) => TrySave();

        Log.Info($"SFHud {AppInfo.Version} starting ({Environment.OSVersion}, {Environment.ProcessorCount} cores)");
        if (migrated is not null) Log.Info(migrated);
        SettingsStore.EnsureFolders();
        DataStore.Load();
        ReducedMotion = Settings.ReducedMotion || !SystemParameters.ClientAreaAnimation;
        ThemeManager.Apply();
        SoundService.EnsureBuiltIns();

        var launcher = new LauncherWindow();
        MainWindow = launcher;
        if (!Settings.StartMinimized) launcher.Show();
        else { launcher.WindowState = WindowState.Minimized; launcher.Show(); }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var t = new Thread(() =>
        {
            var handles = new WaitHandle[] { _showEvent, _exitEvent };
            while (true)
            {
                int i;
                try { i = WaitHandle.WaitAny(handles); } catch (Exception) { return; }
                if (i == 0) Dispatcher.BeginInvoke(() => (MainWindow as LauncherWindow)?.BringToFront());
                else { Dispatcher.BeginInvoke(() => MainWindow?.Close()); return; }
            }
        }) { IsBackground = true, Name = "SFHud.Signals" };
        t.Start();
    }

    private void OnUiCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("ui", e.Exception);
        TrySave();
        if (_crashes == 0 || DateTime.Now - _firstCrash > TimeSpan.FromMinutes(1)) { _crashes = 0; _firstCrash = DateTime.Now; }
        _crashes++;
        if (_crashes > 5)
        {
            MessageBox.Show($"SFHud keeps hitting an error and will close. Your notes and settings were saved.\n\nDetails: {Log.CurrentPath}", "SFHud", MessageBoxButton.OK, MessageBoxImage.Error);
            return; // let it terminate
        }
        e.Handled = true;
        if (_crashes == 1)
            MessageBox.Show($"Something went wrong, but SFHud is still running and your data was saved.\n\n{e.Exception.Message}\n\nDetails are in {Log.CurrentPath}", "SFHud", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void TrySave()
    {
        try { if (DataStore.Data.Profiles.Count > 0) DataStore.SaveNow(); } catch (Exception) { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TrySave();
        Log.Info("SFHud exit");
        _showEvent?.Dispose();
        _exitEvent?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
