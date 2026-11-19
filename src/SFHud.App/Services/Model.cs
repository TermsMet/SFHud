using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SFHud.Services;

// =====================================================================================
//  SFHud's saved data, top to bottom:
//    UserData            one file per Windows user (%LOCALAPPDATA%\SFHud\userdata.json)
//     └ UserProfile      personal configuration (Main RP, Training, Event…): settings, keybinds, presets
//        └ ServerPreset  one server / RP style: theme, DEFCON levels, overlay pages, note pads
//           ├ OverlayPage   elements with their own position, size, colours and visibility
//           ├ NotePad       notes (formatted text), window geometry, keybinds via KeyBind actions
//           └ DefconConfig  levels, detection sources, the active level
//  Nothing is shared between presets or profiles, so switching never overwrites another one's data.
// =====================================================================================

public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("N")[..12];
}

public enum ElementKind { Strip, Clock, Timers, Defcon, Intel, NotePad, InfoCard, Media, Text, Note, Video }
public enum BorderKind { Hud, Line, Rounded, None }
public enum HudCorners { Chamfer, Rounded, Square }
public enum PresetStyle { Swrp, Zombie, Military, General, Custom }
public enum TimerMode { Countdown, Stopwatch }
/// <summary>Which player the music controls follow. ChromeBrave is the 1.0 value; it now means any Chromium browser.</summary>
public enum MediaMode { SteamBrowser, ChromeBrave, AnyPlayer, Auto, Chrome, Brave, Edge, Opera, Vivaldi, Firefox, Spotify }
public enum AutoApplyMode { Never, HighConfidence }

public sealed class OverlayElement
{
    public string Id { get; set; } = Ids.New();
    public ElementKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Position as a fraction of the free space (0,0 top-left … 1,1 bottom-right), so it survives resolution changes.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Size in 1080p reference units; the overlay scales with the game's height.</summary>
    public double W { get; set; } = 280;
    /// <summary>0 = fit content.</summary>
    public double H { get; set; }
    public bool Visible { get; set; } = true;

    // Look ("" = follow the theme)
    public string Accent { get; set; } = "";
    public string Background { get; set; } = "";
    public string Foreground { get; set; } = "";
    public string FontFamily { get; set; } = "";
    public double FontSize { get; set; } = 12.5;
    public double Opacity { get; set; } = 1;
    public double BackgroundOpacity { get; set; } = 0.88;
    public BorderKind Border { get; set; } = BorderKind.Hud;
    public double BorderThickness { get; set; } = 1;
    public double CornerRadius { get; set; } = 8;
    public double Padding { get; set; } = 12;
    public bool ShowTitle { get; set; } = true;

    // Content
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    /// <summary>Formatted text for info cards and text labels (see NoteMarkup).</summary>
    public string Text { get; set; } = "";
    public string PadId { get; set; } = "";
    public int MaxItems { get; set; } = 4;
    /// <summary>Note elements: the one note they show (popped out from a pad).</summary>
    public string NoteId { get; set; } = "";
    /// <summary>The game whose HUD this element's saved position has already been checked against
    /// (moved clear once, or placed by hand). After that it never moves by itself.</summary>
    public string HudClearedFor { get; set; } = "";
    /// <summary>Video elements: a local file (mp4, wmv, webm…) or a direct http(s) video link.</summary>
    public string VideoSource { get; set; } = "";
    public bool VideoLoop { get; set; } = true;
    public bool VideoMuted { get; set; } = true;
    public double VideoVolume { get; set; } = 0.5;

    /// <summary>Only shown while one of these DEFCON levels is active (empty = always).</summary>
    public List<string> DefconFilter { get; set; } = new();

    public OverlayElement Clone(bool newId = true)
    {
        var c = DataStore.DeepClone(this);
        if (newId) c.Id = Ids.New();
        return c;
    }

    [JsonIgnore] public string KindName => ElementCatalog.Name(Kind);
    [JsonIgnore] public string DisplayName => Name.Length > 0 ? Name : KindName;
}

public sealed class OverlayPage
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Page";
    public List<OverlayElement> Elements { get; set; } = new();
}

public sealed class LayoutSnapshot
{
    public string Name { get; set; } = "";
    public DateTime Saved { get; set; } = DateTime.Now;
    public OverlayPage Page { get; set; } = new();
}

public sealed class PadNote
{
    public string Id { get; set; } = Ids.New();
    public DateTime Created { get; set; } = DateTime.Now;
    public DateTime Modified { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    public bool Pinned { get; set; }
    /// <summary>Optional coloured label (hex) and its short text.</summary>
    public string LabelColor { get; set; } = "";
    public string Label { get; set; } = "";
    public bool HiddenOnOverlay { get; set; }

    [JsonIgnore] public string Time => Created.Date == DateTime.Today ? Created.ToString("HH:mm") : Created.ToString("MMM d · HH:mm");
    [JsonIgnore] public string FirstLine => NoteMarkup.PlainText(Text.Split('\n')[0]).Trim();
}

public sealed class NotePad
{
    public string Id { get; set; } = Ids.New();
    public string Title { get; set; } = "Notes";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "";
    public string Category { get; set; } = "General";
    public bool Pinned { get; set; }
    /// <summary>"" = shown at every DEFCON level; otherwise only while that level is active.</summary>
    public string DefconLevelId { get; set; } = "";
    public List<PadNote> Notes { get; set; } = new();
    /// <summary>Deleted / cleared notes, newest first, so a mis-click can be undone.</summary>
    public List<PadNote> Trash { get; set; } = new();
    public bool ConfirmDelete { get; set; } = true;
    public double FontSize { get; set; } = 13;
    public double LineSpacing { get; set; } = 1.25;

    // Floating window (Ctrl+Alt+J or the pad's own key). Stored per pad.
    public double WinX { get; set; } = double.NaN;
    public double WinY { get; set; } = double.NaN;
    public double WinW { get; set; } = 440;
    public double WinH { get; set; } = 520;
    public double MinW { get; set; } = 300;
    public double MinH { get; set; } = 220;
    public double MaxW { get; set; } = 1600;
    public double MaxH { get; set; } = 1400;
    public double Opacity { get; set; } = 0.94;
    public bool Blur { get; set; } = true;
    public bool Topmost { get; set; } = true;
    public DateTime LastUsed { get; set; } = DateTime.MinValue;
    /// <summary>Unsaved text in the pad editor (kept across restarts).</summary>
    public string Draft { get; set; } = "";
}

public sealed class DefconLevel
{
    public string Id { get; set; } = Ids.New();
    /// <summary>The number servers announce ("DEFCON 3"). Optional for named levels ("LOCKDOWN").</summary>
    public int? Number { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#00E5FF";
    public string Description { get; set; } = "";
    /// <summary>Comma separated extra words that mean this level in chat / on screen.</summary>
    public string Aliases { get; set; } = "";
    /// <summary>Overlay page to switch to when this level becomes active ("" = stay).</summary>
    public string AutoPageId { get; set; } = "";
    public string PadId { get; set; } = "";
    public bool Toast { get; set; } = true;
    public string Sound { get; set; } = "";
    public bool Flash { get; set; } = true;
    /// <summary>Overlay opacity while this level is active (0 = keep the normal value).</summary>
    public double OverlayOpacity { get; set; }

    [JsonIgnore] public string Short => Number is int n ? n.ToString() : (Name.Length > 0 ? Name[..Math.Min(3, Name.Length)].ToUpperInvariant() : "?");
    [JsonIgnore] public string Label => Number is int n ? (Name.Length > 0 && !Name.Contains(n.ToString()) ? $"DEFCON {n} · {Name}" : $"DEFCON {n}") : Name;
}

public sealed class DetectionSettings
{
    public bool Enabled { get; set; } = true;
    public AutoApplyMode AutoApply { get; set; } = AutoApplyMode.Never;
    public double Threshold { get; set; } = 0.9;
    public bool ConsoleLog { get; set; } = true;
    public string StatusUrl { get; set; } = "";
    public int StatusUrlSeconds { get; set; } = 60;
    public bool Ocr { get; set; }
    public double OcrX { get; set; } = 0.40;
    public double OcrY { get; set; } = 0.0;
    public double OcrW { get; set; } = 0.20;
    public double OcrH { get; set; } = 0.06;
    public int OcrSeconds { get; set; } = 5;
    public bool AiAssist { get; set; } = true;
    /// <summary>The local AI switches the level by itself (when the words it cites are really in the text). The "default DEFCON" key undoes it.</summary>
    public bool AiAutoApply { get; set; } = true;
    /// <summary>How often the AI looks at new console / screen / status text, at most.</summary>
    public int AiAutoSeconds { get; set; } = 20;
    /// <summary>Minimum AI confidence for an automatic switch.</summary>
    public double AiMinConfidence { get; set; } = 0.6;

    // ---- Fallback vs. detected (the overlay never presents an assumed level as verified) ----
    /// <summary>On startup / preset switch, show the fallback level labelled ASSUMED until a reading is verified.</summary>
    public bool AssumeOnStartup { get; set; } = true;
    /// <summary>The safe default shown when nothing is verified (DEFCON 6 = peacetime).</summary>
    public int FallbackNumber { get; set; } = 6;
    /// <summary>Apply a screen-region reading automatically once it has been read the same N times in a row.</summary>
    public bool AutoApplyVerifiedScreen { get; set; } = true;
    public int VerifyReads { get; set; } = 3;
    public double VerifyConfidence { get; set; } = 0.8;
    /// <summary>No confirming screen reading for this long → the detected level is shown as STALE.</summary>
    public int StaleAfterSeconds { get; set; } = 30;
    /// <summary>Stale for this long (since the last verified reading) → back to the assumed fallback.</summary>
    public int AssumeAfterSeconds { get; set; } = 180;
}

/// <summary>How much the shown DEFCON level can be trusted.</summary>
public enum DefconStatus
{
    /// <summary>Fallback (DEFCON 6): startup, unknown server, or detection timed out.</summary>
    Assumed,
    /// <summary>Read from the screen region several times in a row, recently.</summary>
    Detected,
    /// <summary>Was detected, but no confirming reading lately; kept for a while, then back to Assumed.</summary>
    Stale,
    /// <summary>Set by you, a suggestion you confirmed, or an opted-in rule match.</summary>
    Set,
    /// <summary>Switched by the local AI from an announcement it read. Stays until something else changes it.</summary>
    AiDetected,
}

public sealed class DefconConfig
{
    public List<DefconLevel> Levels { get; set; } = new();
    public string ActiveLevelId { get; set; } = "";
    public DetectionSettings Detection { get; set; } = new();
    /// <summary>When the new level has no layout of its own, go back to the default page (instead of staying on the old level's page).</summary>
    public bool ReturnToDefaultPage { get; set; } = true;

    public DefconLevel? Active => Levels.FirstOrDefault(l => l.Id == ActiveLevelId);
    public DefconLevel? ByNumber(int n) => Levels.FirstOrDefault(l => l.Number == n);

    public static DefconConfig Default(int count = 6, string[]? colors = null)
    {
        colors ??= new[] { "#FF2D4F", "#FF7A1A", "#FFC400", "#3DA8FF", "#7CFFB2", "#9AA7B4" };
        string[] names = { "Maximum readiness", "High alert", "Elevated", "Guarded", "Normal", "Peacetime" };
        string[] desc =
        {
            "Battle stations. All units armed, base sealed.",
            "Imminent threat. Restricted areas locked down.",
            "Increased security. Patrols doubled, ID checks on.",
            "Possible threat. Stay alert, report suspicious activity.",
            "Standard operations.",
            "Relaxed operations, training and events allowed.",
        };
        var cfg = new DefconConfig();
        for (int i = 0; i < count; i++)
            cfg.Levels.Add(new DefconLevel { Number = i + 1, Name = i < names.Length ? names[i] : $"Level {i + 1}", Color = colors[Math.Min(i, colors.Length - 1)], Description = i < desc.Length ? desc[i] : "" });
        return cfg;
    }
}

public sealed class InfoCardTemplate
{
    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class ThemeDef
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Custom";
    public string Bg { get; set; } = "#05080C";
    public string Elevated { get; set; } = "#09111A";
    public string Accent { get; set; } = "#00E5FF";
    public string Warn { get; set; } = "#FFC400";
    public string Alert { get; set; } = "#FF2D6F";
    public string Ok { get; set; } = "#7CFFB2";
    public string Text { get; set; } = "#E8FDFF";
    public string Dim { get; set; } = "#7FB8C4";
    public bool Light { get; set; }
    public HudCorners Corners { get; set; } = HudCorners.Chamfer;
    public string DisplayFont { get; set; } = "Orbitron, Bahnschrift, Segoe UI";
    public string BodyFont { get; set; } = "Bahnschrift, Segoe UI";
    public string MonoFont { get; set; } = "Share Tech Mono, Cascadia Mono, Consolas";
}

public sealed class ServerPreset
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Preset";
    public string Icon { get; set; } = "";
    public PresetStyle Style { get; set; } = PresetStyle.Custom;
    public string Description { get; set; } = "";
    public string ThemeId { get; set; } = "swrp";
    public string ServerAddress { get; set; } = "";
    public List<OverlayPage> Pages { get; set; } = new();
    public string DefaultPageId { get; set; } = "";
    public string ActivePageId { get; set; } = "";
    public List<LayoutSnapshot> Layouts { get; set; } = new();
    public List<NotePad> Pads { get; set; } = new();
    public List<string> Categories { get; set; } = new() { "General" };
    public string ActivePadId { get; set; } = "";
    public DefconConfig Defcon { get; set; } = DefconConfig.Default();
    public bool DefconEnabled { get; set; } = true;
    /// <summary>How this server writes chat lines, e.g. "[{role}] {name}: {message}" (SFHud Pro, chat alerts). "" = detect common formats.</summary>
    public string ChatFormat { get; set; } = "";
    /// <summary>What to watch for in chat (SFHud Pro).</summary>
    public List<ChatRule> ChatRules { get; set; } = ChatRule.Defaults();

    [JsonIgnore] public OverlayPage ActivePage => Pages.FirstOrDefault(p => p.Id == ActivePageId) ?? Pages.FirstOrDefault(p => p.Id == DefaultPageId) ?? Pages.First();
    [JsonIgnore] public NotePad? ActivePad => Pads.FirstOrDefault(p => p.Id == ActivePadId) ?? Pads.FirstOrDefault();
    public NotePad? Pad(string id) => Pads.FirstOrDefault(p => p.Id == id);
}

public sealed class KeyBind
{
    public string Action { get; set; } = "";
    /// <summary>"Ctrl+Alt+H"; "" = unbound.</summary>
    public string Gesture { get; set; } = "";
    /// <summary>Only taken while the game (or SFHud) is in front, so other apps keep the combo.</summary>
    public bool InGameOnly { get; set; }
}

public sealed class TimerPreset
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Timer";
    public int Seconds { get; set; } = 300;
    public TimerMode Mode { get; set; } = TimerMode.Countdown;
    public bool Repeat { get; set; }
    public string Color { get; set; } = "";
    public string Sound { get; set; } = "";
}

public sealed class CrosshairPreset
{
    public string Name { get; set; } = "Preset";
    public CrosshairStyle Style { get; set; } = CrosshairStyle.Cross;
    public string Color { get; set; } = "#00E5FF";
    public double Size { get; set; } = 18;
    public double Thickness { get; set; } = 2;
    public double Gap { get; set; } = -1;
    public bool Outline { get; set; } = true;
    public double OutlineThickness { get; set; } = 1;
    public string OutlineColor { get; set; } = "#000000";
    public double Opacity { get; set; } = 1;
    public bool CenterDot { get; set; }
}

public sealed class UserProfile
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Main RP";
    public string Icon { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public AppSettings Settings { get; set; } = new();
    public List<KeyBind> Keybinds { get; set; } = new();
    public List<ServerPreset> Presets { get; set; } = new();
    public string ActivePresetId { get; set; } = "";
    public List<TimerPreset> TimerPresets { get; set; } = new();
    public List<CrosshairPreset> CrosshairPresets { get; set; } = new();
    public List<ThemeDef> CustomThemes { get; set; } = new();
    public List<string> RecentPads { get; set; } = new();

    [JsonIgnore] public ServerPreset ActivePreset => Presets.FirstOrDefault(p => p.Id == ActivePresetId) ?? Presets.First();
}

public sealed class UserData
{
    public const int CurrentSchema = 2;
    public int Schema { get; set; } = CurrentSchema;
    public string AppVersion { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public string ActiveProfileId { get; set; } = "";
    public List<UserProfile> Profiles { get; set; } = new();
}

/// <summary>Display names and defaults for each element type.</summary>
public static class ElementCatalog
{
    public static readonly ElementKind[] Addable =
        { ElementKind.InfoCard, ElementKind.NotePad, ElementKind.Timers, ElementKind.Defcon, ElementKind.Intel, ElementKind.Media, ElementKind.Video, ElementKind.Clock, ElementKind.Strip, ElementKind.Text };

    public static string Name(ElementKind k) => k switch
    {
        ElementKind.Strip => "Status strip",
        ElementKind.Clock => "Clock",
        ElementKind.Timers => "Timers",
        ElementKind.Defcon => "DEFCON badge",
        ElementKind.Intel => "Server intel",
        ElementKind.NotePad => "Note pad",
        ElementKind.InfoCard => "Info card",
        ElementKind.Media => "Music player",
        ElementKind.Text => "Text label",
        ElementKind.Note => "Pinned note",
        ElementKind.Video => "Video",
        _ => k.ToString(),
    };

    public static string Glyph(ElementKind k) => k switch
    {
        ElementKind.Strip => "",
        ElementKind.Clock => "",
        ElementKind.Timers => "",
        ElementKind.Defcon => "",
        ElementKind.Intel => "",
        ElementKind.NotePad => "",
        ElementKind.InfoCard => "",
        ElementKind.Media => "",
        ElementKind.Text => "",
        ElementKind.Note => "",
        ElementKind.Video => "",
        _ => "",
    };

    public static OverlayElement Create(ElementKind k, double x = 0.5, double y = 0.5) => k switch
    {
        ElementKind.Strip => new() { Kind = k, X = x, Y = y, W = 300, FontSize = 11, Padding = 10 },
        ElementKind.Clock => new() { Kind = k, X = x, Y = y, W = 150, FontSize = 22, Padding = 8, Border = BorderKind.None, BackgroundOpacity = 0 },
        ElementKind.Timers => new() { Kind = k, X = x, Y = y, W = 260, Title = "TIMERS", FontSize = 12 },
        ElementKind.Defcon => new() { Kind = k, X = x, Y = y, W = 240, Title = "DEFCON", FontSize = 12 },
        ElementKind.Intel => new() { Kind = k, X = x, Y = y, W = 260, Title = "SERVER INTEL", FontSize = 11.5 },
        ElementKind.NotePad => new() { Kind = k, X = x, Y = y, W = 320, MaxItems = 5, FontSize = 12.5 },
        ElementKind.InfoCard => new() { Kind = k, X = x, Y = y, W = 280, Title = "NEW CARD", Icon = "", Text = "**Edit me** in the customize menu.\n- Bullet\n- [ ] Task" },
        ElementKind.Media => new() { Kind = k, X = x, Y = y, W = 300, Title = "NOW PLAYING", FontSize = 12 },
        ElementKind.Text => new() { Kind = k, X = x, Y = y, W = 260, Text = "## Heading\nYour text", Border = BorderKind.None, BackgroundOpacity = 0.4 },
        ElementKind.Note => new() { Kind = k, X = x, Y = y, W = 300, FontSize = 12.5 },
        ElementKind.Video => new() { Kind = k, X = x, Y = y, W = 420, Title = "VIDEO", Padding = 6, FontSize = 11 },
        _ => new() { Kind = k, X = x, Y = y },
    };
}

/// <summary>Icon choices offered for pads, cards and presets (Segoe Fluent / MDL2 glyphs, present on Windows 10 and 11).</summary>
public static class IconSet
{
    public static readonly (string Glyph, string Name)[] All =
    {
        ("", "Note"), ("", "Card"), ("", "Page"), ("", "Star"), ("", "Pin"),
        ("", "Warning"), ("", "Flag"), ("", "Music"), ("", "Stopwatch"), ("", "Clock"),
        ("", "Shield"), ("", "Group"), ("", "Person"), ("", "Folder"), ("", "Map"),
        ("", "Location"), ("", "Monitor"), ("", "Radio"), ("", "Gear"), ("", "Copy"),
        ("", "Lightning"), ("", "Heart"), ("", "Game"), ("", "Home"), ("", "Target"),
        ("", "Report"), ("", "Library"), ("", "Check"), ("", "Error"), ("", "Info"),
        ("", "Biohazard"), ("", "Lock"), ("", "Chat"), ("", "Globe"), ("", "Eye"),
    };
}

public static class JsonExt
{
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, SettingsStore.Json), SettingsStore.Json)!;
}
