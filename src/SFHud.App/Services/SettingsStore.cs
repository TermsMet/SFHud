using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SFHud.Services;

public enum CrosshairStyle { Dot, Cross, Circle, T, CrossDot, CircleDot, Chevron, Square, Diamond, Brackets, Triangle, X }

public sealed class ServerFavorite
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
}

/// <summary>Per-profile settings. Everything here is saved with the profile in userdata.json.</summary>
public sealed class AppSettings
{
    // Game
    public string TargetId { get; set; } = "gmod";
    public List<GameTarget> CustomTargets { get; set; } = new();
    public Dictionary<string, double> PlaytimeSeconds { get; set; } = new();
    /// <summary>
    /// Process names (no ".exe") the overlay may appear over. The selected game is always allowed;
    /// "Any whitelisted app" follows whichever of these is in front. Nothing else ever gets the overlay.
    /// </summary>
    public List<string> OverlayApps { get; set; } = GameCatalog.DefaultWhitelist();

    // Overlay
    public double OverlayOpacity { get; set; } = 0.95;
    public double UiScale { get; set; } = 1.0;
    /// <summary>Scale the overlay with the game's resolution (1080p = 1×; narrow 4:3 / 5:4 screens are also capped by width).</summary>
    public bool ScaleWithResolution { get; set; } = true;
    /// <summary>
    /// Lowest scale resolution-scaling may reach, so text stays readable at 4:3 / CRT resolutions
    /// (800×600, 1024×768, 1280×960). 1080p is 1×.
    /// </summary>
    public double MinReadableScale { get; set; } = 0.8;
    /// <summary>Scale the in-game menu, toasts and status bar with the screen too (bigger on 1440p / 4K, fitted on 4:3).</summary>
    public bool ScaleOverlayMenu { get; set; } = true;
    public bool PinsVisible { get; set; } = true;
    public bool TimersVisible { get; set; } = true;
    public bool MediaVisible { get; set; } = true;
    /// <summary>Video elements pause when you leave the game and resume when you come back.</summary>
    public bool VideoPauseOutOfGame { get; set; } = true;
    /// <summary>A small play / pause player on the desktop while the overlay is hidden (game not in front).</summary>
    public bool MiniPlayerOutOfGame { get; set; } = true;
    public double MiniPlayerX { get; set; } = double.NaN;
    public double MiniPlayerY { get; set; } = double.NaN;
    /// <summary>Exact spot of the pop-up player in screen units (any monitor). NaN = not moved yet.</summary>
    public double MiniPlayerLeft { get; set; } = double.NaN;
    public double MiniPlayerTop { get; set; } = double.NaN;
    /// <summary>Pad and pinned-note windows hide while another app is in front of the game, and come back with it.</summary>
    public bool PopupsFollowGame { get; set; } = true;
    /// <summary>Keep overlay elements off the game's chat box, so you can always read chat.</summary>
    public bool AvoidGameHud { get; set; } = true;
    /// <summary>SFHud Pro: show chat alerts from the preset's rules.</summary>
    public bool ChatAlertsEnabled { get; set; } = true;
    /// <summary>The local AI reads chat no rule matched and tells you about upcoming events (with a countdown when it says when).</summary>
    public bool ChatEventsAi { get; set; } = true;
    /// <summary>Trial offers already announced with a banner (each is shown once).</summary>
    public List<string> SeenTrialOffers { get; set; } = new();
    /// <summary>Update / news pop-ups already shown (each is shown once).</summary>
    public List<string> SeenAnnouncements { get; set; } = new();
    public bool DistractionFree { get; set; }
    public bool SnapToGrid { get; set; } = true;
    public double GridSize { get; set; } = 8;
    public bool ShowGuides { get; set; } = true;
    public bool LayoutLocked { get; set; }
    public bool ExcludeFromCapture { get; set; }
    public bool MenuOnLeft { get; set; }
    /// <summary>The customize menu takes whichever side covers fewer elements.</summary>
    public bool MenuAutoSide { get; set; } = true;

    // Crosshair (off by default: the center of the screen stays clear)
    public bool CrosshairEnabled { get; set; }
    public CrosshairStyle CrosshairStyle { get; set; } = CrosshairStyle.Cross;
    public string CrosshairColor { get; set; } = "#00E5FF";
    public double CrosshairSize { get; set; } = 18;
    public double CrosshairThickness { get; set; } = 2;
    /// <summary>Center gap in px; -1 = automatic.</summary>
    public double CrosshairGap { get; set; } = -1;
    public bool CrosshairOutline { get; set; } = true;
    public double CrosshairOutlineThickness { get; set; } = 1;
    public string CrosshairOutlineColor { get; set; } = "#000000";
    public double CrosshairOpacity { get; set; } = 1;
    public bool CrosshairCenterDot { get; set; }

    // Intel (alerts, server name) read from Garry's Mod's console.log
    public bool IntelEnabled { get; set; } = true;
    public bool IntelAiEnabled { get; set; }
    public string ConsoleLogPath { get; set; } = "";

    // Local AI
    /// <summary>0 = automatic (a quarter of the CPU threads, 4 to 8: fastest for the small model and leaves the rest to the game).</summary>
    public int AiThreads { get; set; }
    /// <summary>Optional OpenAI-compatible server on this PC (LM Studio, Ollama). Loopback addresses only.</summary>
    public string AiEndpoint { get; set; } = "";
    public string AiExternalModel { get; set; } = "";
    public int AiMaxTokens { get; set; } = 260;
    public double AiTemperature { get; set; } = 0.3;
    public int AiIdleMinutes { get; set; } = 10;

    // Timers + sounds
    public int QuickTimerMinutes { get; set; } = 5;
    public string LastManualTimer { get; set; } = "";
    public string TimerSound { get; set; } = "builtin:soft-chime";
    public string NotifySound { get; set; } = "builtin:glass-ding";
    public string AlertSound { get; set; } = "builtin:gentle-alarm";
    public double SoundVolume { get; set; } = 0.6;
    public bool SoundMuted { get; set; }
    public double ToastSeconds { get; set; } = 4.5;
    public bool Toasts { get; set; } = true;

    // Music player (Windows media sessions)
    public MediaMode MediaMode { get; set; } = MediaMode.Auto;
    public string MediaSessionId { get; set; } = "";

    // Appearance
    /// <summary>"preset" = follow the server preset's theme; otherwise a theme id.</summary>
    public string Theme { get; set; } = "dark";
    public string AccentOverride { get; set; } = "";
    public string BackgroundImage { get; set; } = "";
    public double BackgroundImageOpacity { get; set; } = 0.18;
    public double WindowOpacity { get; set; } = 1.0;
    /// <summary>"" = theme default, or Chamfer / Rounded / Square.</summary>
    public string Corners { get; set; } = "";
    public double CornerRadius { get; set; } = 8;
    public string DisplayFont { get; set; } = "";
    public string BodyFont { get; set; } = "";
    public double FontScale { get; set; } = 1.0;
    /// <summary>Grow the main window's UI on big windows (1440p / 4K) so it doesn't shrink into a strip.</summary>
    public bool AutoScaleLargeWindows { get; set; } = true;
    public string IconPack { get; set; } = "fluent";
    public bool ReducedMotion { get; set; }
    public bool Scanlines { get; set; } = true;
    public bool CompactMode { get; set; }

    // Top bar (status strip) and clocks
    /// <summary>What the top bar shows, in order. See <see cref="TopBar.All"/> for the ids.</summary>
    public List<string> TopBarItems { get; set; } = new() { "game", "preset", "session", "clock", "defcon" };
    public bool Clock24h { get; set; } = true;
    public bool ClockSeconds { get; set; }
    /// <summary>Windows time zone id for clocks ("" = this PC's local time).</summary>
    public string ClockTimeZone { get; set; } = "";
    /// <summary>Optional tag after the time, e.g. "SERVER" or "EST".</summary>
    public string ClockLabel { get; set; } = "";
    /// <summary>"Welcome to SFHud" guide note has been added (only once, so deleting it sticks).</summary>
    public bool WelcomeNoteAdded { get; set; }

    // Behaviour
    public bool ConfirmDeletes { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool AutoEngage { get; set; } = true;
    public string LastPage { get; set; } = "home";
    public double MainW { get; set; } = 1180;
    public double MainH { get; set; } = 760;
    public double MainX { get; set; } = double.NaN;
    public double MainY { get; set; } = double.NaN;
    public int BackupKeep { get; set; } = 20;

    // Servers
    public List<ServerFavorite> Servers { get; set; } = new();
}

/// <summary>Paths and JSON helpers. Everything lives under the current Windows user.</summary>
public static class SettingsStore
{
    /// <summary>SFHUD_DATA_DIR (or the 1.0 name SHUD_DATA_DIR) overrides the location (portable use, testing a build next to a running copy).</summary>
    public static string? CustomDataDir { get; } =
        Environment.GetEnvironmentVariable("SFHUD_DATA_DIR") is { Length: > 0 } a ? a :
        Environment.GetEnvironmentVariable("SHUD_DATA_DIR") is { Length: > 0 } b ? b : null;

    public static string DataDir { get; } = CustomDataDir ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SFHud");

    /// <summary>Where 1.0 (released as "SHud") kept its data.</summary>
    public static string RenamedDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SHud");

    /// <summary>
    /// One-time move of the 1.0 data folder (%LOCALAPPDATA%\SHud) to %LOCALAPPDATA%\SFHud. Runs before
    /// anything writes to the data folder. Renames when it can; otherwise copies without overwriting
    /// and leaves the old folder alone. Returns a line for the log, or null if nothing was done.
    /// </summary>
    public static string? MigrateRenamedDataDir()
    {
        if (CustomDataDir is not null) return null;
        string from = RenamedDataDir;
        try
        {
            if (!File.Exists(Path.Combine(from, "userdata.json")) || File.Exists(Path.Combine(DataDir, "userdata.json"))) return null;
            if (!Directory.Exists(DataDir))
            {
                try { Directory.Move(from, DataDir); return $"moved data from {from}"; }
                catch (IOException) { }               // a file is still open: copy instead
                catch (UnauthorizedAccessException) { }
            }
            int n = CopyTree(from, DataDir);
            return $"copied {n} files from {from} (the old folder was kept)";
        }
        catch (Exception ex) { return "data migration failed: " + ex.Message; }
    }

    private static int CopyTree(string from, string to)
    {
        int n = 0;
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
        {
            string dest = Path.Combine(to, Path.GetFileName(f));
            if (File.Exists(dest)) continue;
            try { File.Copy(f, dest); n++; } catch (IOException) { }
        }
        foreach (var d in Directory.GetDirectories(from)) n += CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
        return n;
    }

    public static string LegacyDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SFHud");

    public static string ModelsDir => Path.Combine(DataDir, "models");
    public static string RuntimeDir => Path.Combine(DataDir, "runtime");
    public static string BackupDir => Path.Combine(DataDir, "backups");
    public static string LogDir => Path.Combine(DataDir, "logs");
    public static string SoundsDir => Path.Combine(DataDir, "sounds");
    public static string LegacySettingsPath => Path.Combine(DataDir, "settings.json");
    public static string LegacyNotesPath => Path.Combine(DataDir, "notes.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static void EnsureFolders()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(SoundsDir);
    }

    /// <summary>Atomic write: temp file then replace, so a crash mid-write never leaves half a file.</summary>
    public static bool WriteJson<T>(string path, T value)
    {
        try
        {
            EnsureFolders();
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("write " + Path.GetFileName(path), ex);
            return false;
        }
    }

    public static bool HasLocalModel()
    {
        try { return Directory.Exists(ModelsDir) && Directory.GetFiles(ModelsDir, "*.gguf").Length > 0; }
        catch (Exception) { return false; }
    }
}
