using System.Collections.Generic;
using System.Linq;

namespace SFHud.Services;

/// <summary>
/// Built-in themes and server presets. Each preset has its own look, pages, pads, cards and DEFCON
/// table. They hold structure and examples only; players fill in their own server's details.
/// </summary>
public static class PresetCatalog
{
    // ================================================================ themes

    public static readonly ThemeDef[] Themes =
    {
        new() { Id = "dark", Name = "SFHud Classic (dark cyan)", Bg = "#05080C", Elevated = "#09111A", Accent = "#00E5FF", Warn = "#FFC400", Alert = "#FF2D6F", Ok = "#7CFFB2", Text = "#E8FDFF", Dim = "#7FB8C4", Corners = HudCorners.Chamfer },
        new() { Id = "swrp", Name = "Republic (SWRP)", Bg = "#060A12", Elevated = "#0B1422", Accent = "#4FB3FF", Warn = "#FFC400", Alert = "#FF4D5E", Ok = "#7CFFB2", Text = "#E6F2FF", Dim = "#8AA6C4", Corners = HudCorners.Chamfer },
        new() { Id = "light", Name = "Light", Bg = "#EEF1F5", Elevated = "#FFFFFF", Accent = "#0B63E5", Warn = "#A86400", Alert = "#C8243F", Ok = "#13804A", Text = "#121820", Dim = "#4D5A6A", Light = true, Corners = HudCorners.Rounded, DisplayFont = "Segoe UI Variable Display, Segoe UI", BodyFont = "Segoe UI Variable Text, Segoe UI", MonoFont = "Cascadia Mono, Consolas" },
        new() { Id = "midnight", Name = "Midnight", Bg = "#06070F", Elevated = "#0D0F21", Accent = "#8C9EFF", Warn = "#FFCB6B", Alert = "#FF6B8B", Ok = "#8BE9B0", Text = "#E8EAFF", Dim = "#8D93C0", Corners = HudCorners.Rounded },
        new() { Id = "darkred", Name = "Dark red", Bg = "#0A0505", Elevated = "#160A0A", Accent = "#E0313B", Warn = "#FF8A3D", Alert = "#FF2A2A", Ok = "#9BD67A", Text = "#F3E6E6", Dim = "#B38A8A", Corners = HudCorners.Square, DisplayFont = "Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI", MonoFont = "Consolas, Cascadia Mono" },
        new() { Id = "blue", Name = "Blue", Bg = "#040912", Elevated = "#09142A", Accent = "#2F8CFF", Warn = "#FFC14D", Alert = "#FF4F6D", Ok = "#59E3A0", Text = "#E7F0FF", Dim = "#8AA2C8", Corners = HudCorners.Rounded },
        new() { Id = "purple", Name = "Purple", Bg = "#0A0612", Elevated = "#140C22", Accent = "#B57BFF", Warn = "#FFC46B", Alert = "#FF5C93", Ok = "#7CF0B5", Text = "#F1E9FF", Dim = "#A895C4", Corners = HudCorners.Chamfer },
        new() { Id = "zombie", Name = "Zombie Survival", Bg = "#0B0606", Elevated = "#170B0B", Accent = "#C8242E", Warn = "#E8803A", Alert = "#FF2A2A", Ok = "#8FC46A", Text = "#EFE2DE", Dim = "#A88480", Corners = HudCorners.Square, DisplayFont = "Bahnschrift SemiBold Condensed, Bahnschrift, Segoe UI", BodyFont = "Bahnschrift, Segoe UI", MonoFont = "Consolas, Cascadia Mono" },
        new() { Id = "military", Name = "Military", Bg = "#0E100C", Elevated = "#181B14", Accent = "#A3B26B", Warn = "#CDB57E", Alert = "#D9573B", Ok = "#8FBF6A", Text = "#E7E4D6", Dim = "#9C9A86", Corners = HudCorners.Square, DisplayFont = "Bahnschrift SemiCondensed, Bahnschrift, Segoe UI", BodyFont = "Bahnschrift, Segoe UI", MonoFont = "Consolas, Cascadia Mono" },
        new() { Id = "general", Name = "General (neutral)", Bg = "#0F1115", Elevated = "#171A20", Accent = "#8AA9E6", Warn = "#E6C06B", Alert = "#E66B7A", Ok = "#7FD1A0", Text = "#E9ECF1", Dim = "#9299A6", Corners = HudCorners.Rounded, DisplayFont = "Segoe UI Variable Display, Segoe UI", BodyFont = "Segoe UI Variable Text, Segoe UI", MonoFont = "Cascadia Mono, Consolas" },
    };

    public static ThemeDef? Theme(string id) => Themes.FirstOrDefault(t => t.Id == id);

    // ================================================================ presets

    public static readonly (PresetStyle Style, string Name, string Description)[] BuiltIns =
    {
        (PresetStyle.Swrp, "Star Wars RP", "Republic blue sci-fi HUD: mission briefings, DEFCON, training and operation info."),
        (PresetStyle.Zombie, "Zombie Survival", "Dark red survival HUD: wave timers, objectives, threat cards and emergency alerts."),
        (PresetStyle.Military, "Military RP", "Olive and tan tactical HUD: op orders, squad, radio frequencies, deployment and equipment."),
        (PresetStyle.General, "General RP", "Neutral, minimal HUD for any RP server: note pads, timers and custom panels."),
    };

    public static ServerPreset Create(PresetStyle style) => style switch
    {
        PresetStyle.Zombie => Zombie(),
        PresetStyle.Military => Military(),
        PresetStyle.General => General(),
        _ => Swrp(),
    };

    public static List<ServerPreset> CreateAll() => BuiltIns.Select(b => Create(b.Style)).ToList();

    private static NotePad Pad(string title, string icon, string category, string color = "", params string[] notes)
    {
        var pad = new NotePad { Title = title, Icon = icon, Category = category, Color = color };
        foreach (var n in notes.Reverse()) pad.Notes.Add(new PadNote { Text = n });
        return pad;
    }

    private static OverlayElement Card(string title, string icon, string text, double x, double y, double w = 280, string accent = "") =>
        new() { Kind = ElementKind.InfoCard, Title = title, Icon = icon, Text = text, X = x, Y = y, W = w, Accent = accent };

    private static OverlayElement El(ElementKind k, double x, double y, double w = 0, string title = "")
    {
        var e = ElementCatalog.Create(k, x, y);
        if (w > 0) e.W = w;
        if (title.Length > 0) e.Title = title;
        return e;
    }

    private static OverlayElement PadEl(NotePad pad, double x, double y, double w = 320, int items = 4) =>
        new() { Kind = ElementKind.NotePad, PadId = pad.Id, X = x, Y = y, W = w, MaxItems = items };

    /// <summary>The guide pinned at the very top of every preset's first pad.</summary>
    public const string WelcomeText =
        "# Welcome to SFHud\n" +
        "Your in-game field manual and HUD for Garry's Mod. It also works over any other game.\n" +
        "## Getting started\n" +
        "- **Alt+~** customize the overlay in game: drag, resize, add and style elements\n" +
        "- **Ctrl+Alt+N** quick note into the current pad (Enter saves · Tab switches pad · Esc closes)\n" +
        "- **Ctrl+Alt+J** open / close these note pads (Esc or Ctrl+W also closes)\n" +
        "- **Ctrl+T** quick timer in game · **Ctrl+Alt+T** type one, e.g. `7m30s patrol` or `@21:30 event`\n" +
        "- **Ctrl+Alt+K** open the SFHud window · **Ctrl+Alt+Backspace** hide everything\n" +
        "## DEFCON\n" +
        "- Starts on **DEFCON 6 · ASSUMED** until a level is verified\n" +
        "- DEFCON › Detection › **Pick region in game** reads your server's DEFCON from the screen\n" +
        "- **Ctrl+Alt+PageUp / PageDown** sets it yourself\n" +
        "## Music\n" +
        "- Home › Now Playing › **SOURCE** picks Steam browser, Chrome, Brave, Edge, Firefox or Spotify\n" +
        "- **Ctrl+Alt+Space** play / pause · **Ctrl+Alt+M** show / hide the player\n" +
        "## Tips\n" +
        "- Overlay › Display › **Top bar & clock** chooses what the top bar shows (game, preset, session, clock…)\n" +
        "- All keys can be changed in Overlay › Keybinds\n" +
        "- Everything saves automatically. Pin, unpin or delete this note any time.";

    public static bool IsWelcome(PadNote n) => n.Text.StartsWith("# Welcome to SFHud") || n.Text.StartsWith("## Welcome to SFHud");

    /// <summary>Puts the welcome guide, pinned, at the top of the preset's first pad (replacing an older one).</summary>
    public static void AddWelcome(ServerPreset p)
    {
        var pad = p.Pads.FirstOrDefault(x => x.Pinned) ?? p.Pads.FirstOrDefault();
        if (pad is null) return;
        pad.Notes.RemoveAll(IsWelcome);
        // Hidden on the in-game overlay so the long guide doesn't cover the HUD; it shows in the pads.
        pad.Notes.Insert(0, new PadNote { Text = WelcomeText, Pinned = true, Label = "GUIDE", LabelColor = "#00E5FF", HiddenOnOverlay = true });
    }

    private static ServerPreset Finish(ServerPreset p)
    {
        AddWelcome(p);
        p.DefaultPageId = p.ActivePageId = p.Pages[0].Id;
        p.ActivePadId = p.Pads[0].Id;
        p.Categories = p.Pads.Select(x => x.Category).Distinct().ToList();
        return p;
    }

    private static ServerPreset Swrp()
    {
        var quick = Pad("Quick Notes", "", "General", "",
            "## Welcome to SFHud\n**Alt+~** customizes the overlay · **Ctrl+Alt+N** adds a note · **Ctrl+Alt+J** opens your pads.",
            "- [ ] Check in with your CO\n- [ ] Read the current briefing");
        var brief = Pad("Mission Briefing", "", "Operations", "#4FB3FF",
            "# Operation\n**Objective:** paste the briefing here\n**Location:** \n**Callsign:** ");
        var training = Pad("Training", "", "Training", "#7CFFB2", "## Training notes\n1. Formation drills\n2. ID check procedure\n3. Arrest procedure");
        var personal = Pad("Personal", "", "Personal", "#B57BFF", "**Rank:** \n**Unit:** \n**Designation:** ");

        var p = new ServerPreset
        {
            Name = "Star Wars RP", Style = PresetStyle.Swrp, ThemeId = "swrp", Icon = "",
            Description = BuiltIns[0].Description,
            Pads = { quick, brief, training, personal },
            Defcon = DefconConfig.Default(6),
        };
        p.Pages.Add(new OverlayPage
        {
            Name = "Main HUD",
            Elements =
            {
                El(ElementKind.Strip, 0.012, 0.018),
                El(ElementKind.Defcon, 1, 0, 240),
                PadEl(quick, 0.5, 0, 340, 3),
                El(ElementKind.Timers, 1, 0.42, 250),
                El(ElementKind.Media, 0, 1, 300),
            },
        });
        p.Pages.Add(new OverlayPage
        {
            Name = "Mission Information",
            Elements =
            {
                PadEl(brief, 0, 0.12, 380, 3),
                Card("OPERATION", "", "**Op name:** \n**Phase:** staging\n- [ ] Briefing attended\n- [ ] Gear check", 1, 0.12, 280),
                Card("RESTRICTED AREAS", "", "Add your server's restricted zones and ship locations.", 1, 0.55, 280, "#FFC400"),
                El(ElementKind.Defcon, 0.5, 0, 240),
                El(ElementKind.Timers, 0, 0.75, 250),
            },
        });
        p.Pages.Add(new OverlayPage
        {
            Name = "Training Information",
            Elements =
            {
                PadEl(training, 0, 0.15, 340, 4),
                Card("ID CHECK", "", "1. Stop and identify\n2. Verify clearance\n3. Log or escalate", 1, 0.15),
                Card("PRISONER TRANSFER", "", "1. Restrain and escort\n2. Holding-cell route\n3. Hand-off and log", 1, 0.5, 280, "#FFC400"),
                El(ElementKind.Timers, 0.5, 0, 240),
            },
        });
        p.Pages.Add(new OverlayPage
        {
            Name = "Personal Information",
            Elements = { PadEl(personal, 0, 0.2, 300, 4), El(ElementKind.Clock, 1, 0, 150), El(ElementKind.Strip, 0.012, 0.018) },
        });
        // Patrol pages when the alert goes up.
        p.Defcon.Levels[0].AutoPageId = p.Pages[1].Id;
        p.Defcon.Levels[1].AutoPageId = p.Pages[1].Id;
        p.Defcon.Levels[0].Sound = "builtin:gentle-alarm";
        p.Defcon.Levels[1].Sound = "builtin:gentle-alarm";
        return Finish(p);
    }

    private static ServerPreset Zombie()
    {
        var notes = Pad("Survival Notes", "", "Survival", "#C8242E", "- [ ] Find ammo\n- [ ] Regroup at the safehouse", "**Safe spots:** ");
        var plan = Pad("Barricade Plan", "", "Survival", "#E8803A", "## Primary hold\n\n## Fallback\n\n## Nails / ammo check");
        var p = new ServerPreset
        {
            Name = "Zombie Survival", Style = PresetStyle.Zombie, ThemeId = "zombie", Icon = "",
            Description = BuiltIns[1].Description,
            Pads = { notes, plan },
            Defcon = new DefconConfig
            {
                Levels =
                {
                    new() { Number = 1, Name = "Overrun", Color = "#FF2A2A", Description = "Fall back. Last stand.", Aliases = "overrun, last stand", Sound = "builtin:gentle-alarm" },
                    new() { Number = 2, Name = "Horde", Color = "#E8803A", Description = "Big wave inbound. Hold the barricades.", Aliases = "horde, wave incoming" },
                    new() { Number = 3, Name = "Stirring", Color = "#CDB57E", Description = "Infected nearby. Stay in groups." },
                    new() { Number = 4, Name = "Quiet", Color = "#8FC46A", Description = "Loot, repair and resupply." },
                },
            },
        };
        p.Pages.Add(new OverlayPage
        {
            Name = "Survival HUD",
            Elements =
            {
                El(ElementKind.Strip, 0.012, 0.018),
                new() { Kind = ElementKind.Timers, Title = "WAVE TIMERS", X = 0.5, Y = 0, W = 260, Accent = "#E8803A" },
                Card("OBJECTIVES", "", "- [ ] Survive the wave\n- [ ] Escort survivors\n- [ ] Reach extraction", 1, 0.1, 270),
                Card("THREATS", "", "[color=#FF2A2A]**Boss:**[/color] not seen\n**Specials:** none reported", 1, 0.5, 270, "#FF2A2A"),
                PadEl(notes, 0, 0.35, 300, 4),
                new() { Kind = ElementKind.Defcon, Title = "THREAT LEVEL", X = 0, Y = 0.12, W = 230 },
            },
        });
        p.Pages.Add(new OverlayPage
        {
            Name = "Barricade Plan",
            Elements = { PadEl(plan, 0, 0.2, 360, 3), El(ElementKind.Timers, 0.5, 0, 260, "WAVE TIMERS") },
        });
        return Finish(p);
    }

    private static ServerPreset Military()
    {
        var orders = Pad("Op Orders", "", "Operations", "#A3B26B", "# OPORD\n**Situation:** \n**Mission:** \n**Execution:** \n**Sustainment:** \n**Command / signal:** ");
        var equip = Pad("Equipment", "", "Logistics", "#CDB57E", "- [ ] Primary + 4 mags\n- [ ] Sidearm\n- [ ] Radio checked\n- [ ] Med kit");
        var deploy = Pad("Deployment", "", "Operations", "#8FBF6A", "**Grid:** \n**Rally point:** \n**Exfil:** ");
        var p = new ServerPreset
        {
            Name = "Military RP", Style = PresetStyle.Military, ThemeId = "military", Icon = "",
            Description = BuiltIns[2].Description,
            Pads = { orders, equip, deploy },
            Defcon = DefconConfig.Default(5, new[] { "#D9573B", "#E0893E", "#CDB57E", "#A3B26B", "#8FBF6A" }),
        };
        p.Pages.Add(new OverlayPage
        {
            Name = "Operations",
            Elements =
            {
                El(ElementKind.Strip, 0.012, 0.018),
                Card("MISSION OBJECTIVES", "", "1. Secure the objective\n2. Hold until relieved\n- [ ] Report SITREP", 1, 0.08, 280),
                Card("SQUAD", "", "**SL:** \n**FTL:** \n**Medic:** \n**RTO:** ", 1, 0.42, 280),
                Card("RADIO FREQUENCIES", "", "**Command:** 40.0\n**Squad:** 41.5\n**Air:** 50.0", 0, 0.5, 240),
                Card("STATUS", "", "[color=#8FBF6A]**GREEN**[/color] · ready to move", 0, 0.12, 240),
                new() { Kind = ElementKind.Timers, Title = "OP TIMERS", X = 0.5, Y = 0, W = 250 },
                new() { Kind = ElementKind.Defcon, Title = "READINESS", X = 0.5, Y = 1, W = 240 },
            },
        });
        p.Pages.Add(new OverlayPage
        {
            Name = "Deployment",
            Elements = { PadEl(deploy, 0, 0.15, 300), PadEl(equip, 1, 0.15, 280), PadEl(orders, 0.5, 0.6, 380, 2) },
        });
        return Finish(p);
    }

    private static ServerPreset General()
    {
        var notes = Pad("Notes", "", "General", "", "Write anything here. **Bold**, *italic*, __underline__, ~~strike~~, ==highlight==.\n- [ ] Tasks work too");
        var p = new ServerPreset
        {
            Name = "General RP", Style = PresetStyle.General, ThemeId = "general", Icon = "",
            Description = BuiltIns[3].Description,
            Pads = { notes },
            DefconEnabled = false,
            Defcon = DefconConfig.Default(6),
        };
        p.Pages.Add(new OverlayPage
        {
            Name = "Main",
            Elements =
            {
                El(ElementKind.Strip, 0.012, 0.018),
                PadEl(notes, 0.5, 0, 340, 3),
                El(ElementKind.Timers, 1, 0, 240),
                Card("INFO", "", "Add your own panels in the customize menu (**Alt+~**).", 1, 0.4, 260),
            },
        });
        p.Pages.Add(new OverlayPage { Name = "Custom Layout", Elements = { El(ElementKind.Clock, 1, 0, 150) } });
        return Finish(p);
    }

    // ================================================================ timers + keybind defaults

    public static List<TimerPreset> DefaultTimers() => new()
    {
        new() { Name = "Patrol", Seconds = 600, Repeat = true, Color = "#4FB3FF" },
        new() { Name = "Wave", Seconds = 90, Repeat = true, Color = "#E8803A" },
        new() { Name = "Break", Seconds = 300 },
        new() { Name = "Stopwatch", Mode = TimerMode.Stopwatch, Color = "#7CFFB2" },
    };

    public static List<CrosshairPreset> DefaultCrosshairs() => new()
    {
        new() { Name = "Classic cross", Style = CrosshairStyle.Cross, Color = "#00E5FF" },
        new() { Name = "Tiny dot", Style = CrosshairStyle.Dot, Color = "#7CFFB2", Size = 10 },
        new() { Name = "Ring + dot", Style = CrosshairStyle.CircleDot, Color = "#FFC400", Size = 22 },
    };
}
