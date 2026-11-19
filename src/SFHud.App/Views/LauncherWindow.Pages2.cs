using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SFHud.Controls;
using SFHud.Interop;
using SFHud.Services;

namespace SFHud.Views;

public partial class LauncherWindow
{
    private readonly ObservableCollection<ServerEntry> _servers = new();
    private readonly HashSet<string> _openSections = new();

    // ================================================================ TIMERS

    private FrameworkElement TimersPage()
    {
        var prof = DataStore.Profile;
        var sp = new StackPanel();
        sp.Children.Add(PageHeader("Timers", "Run several at once: countdowns, repeating rounds and stopwatches. Each preset can have its own key.",
            Ui.Button("STOP ALL", () => TimerService.Instance.CancelAll(), "Sf.Button.Small.Danger")));

        var running = Card("RUNNING", $"{Gesture("timer.quick")} starts a {S.QuickTimerMinutes}-minute timer in game · {Gesture("timer.manual")} lets you type one.",
            TimerList(), Ui.Spacer(6), TimerEntry(),
            Ui.Slider("Quick timer length", 1, 60, S.QuickTimerMinutes, v => $"{v:0} min", v => { S.QuickTimerMinutes = (int)v; DataStore.MarkDirty(); }, step: 1));

        var presets = new StackPanel();
        foreach (var tp in prof.TimerPresets)
        {
            var t = tp;
            var head = new DockPanel();
            var sw = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = ThemeManager.BrushOr(t.Color, "B.Accent"), Margin = new Thickness(0, 0, 10, 0) };
            head.Children.Add(sw);
            head.Children.Add(Ui.H2($"{t.Name.ToUpperInvariant()}  ·  {(t.Mode == TimerMode.Stopwatch ? "STOPWATCH" : TimerParser.Describe(TimeSpan.FromSeconds(t.Seconds)) + (t.Repeat ? " ↻" : ""))}", 11.5));
            var dur = Ui.TextBox(TimerParser.Describe(TimeSpan.FromSeconds(t.Seconds)).ToLowerInvariant().Replace(" ", ""), v =>
            {
                if (TimerParser.TryParse(v, out var r)) { t.Seconds = (int)r.Duration.TotalSeconds; DataStore.MarkDirty(); }
            });
            dur.ToolTip = "e.g. 90s, 5m, 1:30, 1h";
            var body = Ui.Stack(
                Ui.Row("Name", Ui.TextBox(t.Name, v => { t.Name = v.Trim().Length > 0 ? v.Trim() : t.Name; DataStore.MarkDirty(); })),
                Ui.Row("Mode", Ui.Combo(Enum.GetValues<TimerMode>(), t.Mode, v => { t.Mode = v; DataStore.MarkDirty(); })),
                Ui.Row("Length", dur),
                Ui.Switch("Repeat (rounds)", t.Repeat, v => { t.Repeat = v; DataStore.MarkDirty(); }),
                Ui.Label("COLOUR"), Ui.ColorPicker(t.Color, v => { t.Color = v; DataStore.MarkDirty(); }),
                Ui.Row("Sound", SoundCombo(t.Sound, v => { t.Sound = v; DataStore.MarkDirty(); }, allowDefault: true)),
                Ui.Label("KEY: " + (App.Gesture("timer." + t.Id) is { Length: > 0 } g ? g : "none · set one in Overlay › Keybinds › Timer presets")),
                Ui.Wrap(Ui.Button("START", () => TimerService.Instance.Start(t), "Sf.Button.Small.Primary"),
                        Ui.Button("DELETE", () => { prof.TimerPresets.Remove(t); prof.Keybinds.RemoveAll(k => k.Action == "timer." + t.Id); DataStore.NotifyContext(); }, "Sf.Button.Small.Danger")));
            presets.Children.Add(new Expander { Header = head, Content = body });
        }
        var presetCard = Card("PRESETS", "Saved timers with their own name, colour, sound and key.", presets,
            Ui.Button("+ NEW PRESET", () => { prof.TimerPresets.Add(new TimerPreset { Name = DataStore.UniqueName("Timer", prof.TimerPresets.Select(x => x.Name)) }); DataStore.NotifyContext(); }, "Sf.Button.Small.Primary"));

        sp.Children.Add(Columns(360, running, presetCard, SoundsCard()));
        return sp;
    }

    private HudFrame SoundsCard()
    {
        var list = new StackPanel();
        foreach (var s in SoundService.All())
        {
            var snd = s;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var acts = Ui.HStack(Ui.IconButton("", "Preview", () => SoundService.Play(snd.Id, force: true), 11));
            if (!s.BuiltIn) acts.Children.Add(Ui.IconButton("", "Delete this sound", () => { SoundService.Delete(snd.Id); RenderPage(); }, 11));
            DockPanel.SetDock(acts, Dock.Right);
            row.Children.Add(acts);
            row.Children.Add(Ui.Text(s.Name, 12.5, s.BuiltIn ? "B.Text" : "B.Accent", wrap: false));
            list.Children.Add(row);
        }
        return Card("SOUNDS", "Built-in soft beeps are made by SFHud itself. Add your own .wav / .mp3 (for example free sounds from Pixabay).",
            Ui.Row("Timer done", SoundCombo(S.TimerSound, v => { S.TimerSound = v; DataStore.MarkDirty(); }, allowDefault: false)),
            Ui.Row("Notifications", SoundCombo(S.NotifySound, v => { S.NotifySound = v; DataStore.MarkDirty(); }, allowDefault: false)),
            Ui.Row("Alerts / DEFCON up", SoundCombo(S.AlertSound, v => { S.AlertSound = v; DataStore.MarkDirty(); }, allowDefault: false)),
            Ui.Slider("Volume", 0, 1, S.SoundVolume, v => $"{v:P0}", v => { S.SoundVolume = Math.Round(v, 2); DataStore.MarkDirty(); }),
            Ui.Switch("Mute all SFHud sounds", S.SoundMuted, v => { S.SoundMuted = v; DataStore.MarkDirty(); }),
            list,
            Ui.Wrap(
                Ui.Button("IMPORT SOUND…", () =>
                {
                    var f = Ui.OpenFile("Add a sound", "Audio (*.wav;*.mp3)|*.wav;*.mp3");
                    if (f is null) return;
                    if (new FileInfo(f).Length > 10 * 1024 * 1024) { Ui.Info(this, "Sound", "Pick a file under 10 MB."); return; }
                    string id = SoundService.Import(f);
                    SoundService.Play(id, force: true);
                    RenderPage();
                }, "Sf.Button.Small.Primary"),
                Ui.Button("FREE SOUNDS ON PIXABAY", () => OpenUrl(SoundService.PixabaySearch), tip: "Opens Pixabay's free sound effects in your browser. Download one, then IMPORT SOUND."),
                Ui.Button("OPEN SOUNDS FOLDER", () => OpenUrl(SoundService.CustomDir))));
    }

    // ================================================================ AI

    private static TextBlock Wrapped(TextBlock t) { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; return t; }

    private FrameworkElement AiPage()
    {
        var sp = new StackPanel();
        if (!Edition.Has(ProFeature.LocalAi))
        {
            sp.Children.Add(PageHeader("Local AI", "An offline assistant that runs on your CPU. Part of SFHud Pro."));
            sp.Children.Add(Columns(380, Card("LOCAL AI", null, Ui.ProPanel(ProFeature.LocalAi,
                "Summarizes mission briefings, suggests quick notes, labels and organizes pads, summarizes server chat, checks the DEFCON and answers questions about your notes. " +
                "Runs fully offline on your CPU (or LM Studio / Ollama on this PC), and never changes anything without your click."))));
            return sp;
        }
        return sp;
    }

    // ================================================================ PRESETS + PROFILES

    private FrameworkElement PresetsPage()
    {
        var prof = DataStore.Profile;
        var sp = new StackPanel();
        sp.Children.Add(PageHeader("Presets & profiles", "Server presets hold a look, pages, pads and DEFCON table. Profiles hold everything, including presets and keybinds. Switching never overwrites the others."));

        var presetCards = new List<UIElement>();
        foreach (var pr in prof.Presets)
        {
            var p = pr;
            bool active = p.Id == DataStore.Preset.Id;
            var theme = DataStore.Profile.CustomThemes.FirstOrDefault(t => t.Id == p.ThemeId) ?? PresetCatalog.Theme(p.ThemeId) ?? PresetCatalog.Themes[0];
            var swatches = Ui.HStack(Swatch(theme.Bg), Swatch(theme.Accent), Swatch(theme.Warn), Swatch(theme.Text));
            var head = new DockPanel();
            var ic = Ui.Icon(p.Icon, 18);
            ic.Foreground = Ui.Brush(theme.Accent);
            ic.Margin = new Thickness(0, 0, 10, 0);
            head.Children.Add(ic);
            head.Children.Add(Ui.Text(p.Name + (active ? "   ● ACTIVE" : ""), 15, active ? "B.Accent" : "B.Text"));
            var themes = PresetCatalog.Themes.Select(t => t.Id).Concat(DataStore.Profile.CustomThemes.Select(t => t.Id)).ToList();
            var card = Card(p.Style == PresetStyle.Custom ? "CUSTOM PRESET" : p.Style.ToString().ToUpperInvariant() + " PRESET", null,
                head,
                Ui.Hint(p.Description.Length > 0 ? p.Description : "Your own preset."),
                swatches,
                Ui.Label($"{p.Pages.Count} PAGES · {p.Pads.Count} PADS · {p.Pads.Sum(x => x.Notes.Count)} NOTES · {(p.DefconEnabled ? p.Defcon.Levels.Count + " DEFCON LEVELS" : "DEFCON OFF")}"),
                Ui.Spacer(6),
                Ui.Row("Theme", Ui.Combo(themes, p.ThemeId, v => { p.ThemeId = v; DataStore.MarkDirty(); if (active) ThemeManager.Apply(); RenderPage(); },
                    id => DataStore.Profile.CustomThemes.FirstOrDefault(t => t.Id == id)?.Name ?? PresetCatalog.Theme(id)?.Name ?? id), "Used when Settings › Appearance › Theme is \"Follow preset\"."),
                Ui.Row("Server address", Ui.TextBox(p.ServerAddress, v => { p.ServerAddress = v.Trim(); DataStore.MarkDirty(); }), "Optional, for your reference and the Connect button."),
                Ui.Wrap(
                    active ? Ui.Spacer(0) : Ui.Button("USE", () => DataStore.SwitchPreset(p), "Sf.Button.Small.Primary"),
                    Ui.Button("RENAME", () => { if (Ui.Prompt(this, "Rename preset", p.Name) is { Length: > 0 } n) { p.Name = n.Trim(); DataStore.NotifyContext(); } }),
                    Ui.Button("ICON", () => IconDialog(p.Icon, g => { p.Icon = g; DataStore.NotifyContext(); })),
                    Ui.Button("DUPLICATE", () => { DataStore.DuplicatePreset(p); RenderPage(); }),
                    Ui.Button("EXPORT", () => { var f = Ui.SaveFile("Export preset", Ui.SafeFileName(p.Name) + ".sfhudpreset", "SFHud preset (*.sfhudpreset;*.shudpreset)|*.sfhudpreset;*.shudpreset"); if (f is not null) DataStore.ExportJson(f, "preset", p); }),
                    Ui.Button("DELETE", () =>
                    {
                        if (prof.Presets.Count <= 1) { Ui.Info(this, "Presets", "You need at least one preset."); return; }
                        if (!Ui.Confirm(this, "Delete preset", $"Delete \"{p.Name}\" with its {p.Pads.Sum(x => x.Notes.Count)} notes and {p.Pages.Count} pages? A backup is made first.")) return;
                        DataStore.Backup("preset-deleted");
                        prof.Presets.Remove(p);
                        DataStore.NotifyContext();
                    }, "Sf.Button.Small.Danger")));
            presetCards.Add(card);
        }
        var addPreset = Card("NEW PRESET", "Start from one of the built-in styles (each has its own look and layout), or import one.",
            Ui.Wrap(PresetCatalog.BuiltIns.Select(b => (UIElement)Ui.Button("+ " + b.Name.ToUpperInvariant(), () =>
            {
                var np = PresetCatalog.Create(b.Style);
                np.Name = DataStore.UniqueName(np.Name, prof.Presets.Select(x => x.Name));
                prof.Presets.Add(np);
                DataStore.NotifyContext();
            }, tip: b.Description)).Append(Ui.Button("+ BLANK", () =>
            {
                var np = PresetCatalog.Create(PresetStyle.General);
                np.Name = DataStore.UniqueName("My Server", prof.Presets.Select(x => x.Name));
                np.Style = PresetStyle.Custom;
                np.Description = "";
                np.Pages = new() { new OverlayPage { Name = "Main HUD", Elements = { ElementCatalog.Create(ElementKind.Strip, 0, 0) } } };
                np.Pads[0].Notes.Clear();
                DataStore.NormalizePreset(np);
                np.DefaultPageId = np.ActivePageId = np.Pages[0].Id;
                prof.Presets.Add(np);
                DataStore.NotifyContext();
            })).Append(Ui.Button("IMPORT…", () =>
            {
                var f = Ui.OpenFile("Import preset", "SFHud preset (*.sfhudpreset;*.shudpreset)|*.sfhudpreset;*.shudpreset");
                if (f is null) return;
                try
                {
                    var np = DataStore.Reidentify(DataStore.ImportJson<ServerPreset>(f, "preset") ?? throw new InvalidDataException("Empty file."));
                    np.Name = DataStore.UniqueName(np.Name, prof.Presets.Select(x => x.Name));
                    prof.Presets.Add(np);
                    DataStore.NotifyContext();
                }
                catch (Exception ex) { Ui.Info(this, "Import failed", ex.Message); }
            }, "Sf.Button.Small.Primary")).ToArray()));
        presetCards.Add(addPreset);

        sp.Children.Add(Ui.H2("SERVER PRESETS"));
        sp.Children.Add(Ui.Spacer(8));
        sp.Children.Add(Columns(340, presetCards.ToArray()));

        // Profiles
        var profiles = new StackPanel();
        foreach (var pf in DataStore.Data.Profiles)
        {
            var p = pf;
            bool active = p.Id == DataStore.Profile.Id;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var acts = Ui.Wrap(
                active ? Ui.Spacer(0) : Ui.ProFade(Ui.Button("USE", () => { if (!Edition.ProfileUsable(DataStore.Data.Profiles.IndexOf(p))) { Ui.RequirePro(ProFeature.UnlimitedProfiles); return; } DataStore.SwitchProfile(p); }, "Sf.Button.Small.Primary"), ProFeature.UnlimitedProfiles, !Edition.ProfileUsable(DataStore.Data.Profiles.IndexOf(p))),
                Ui.Button("RENAME", () => { if (Ui.Prompt(this, "Rename profile", p.Name) is { Length: > 0 } n) { p.Name = n.Trim(); DataStore.NotifyContext(); } }),
                Ui.Button("DUPLICATE", () => { DataStore.DuplicateProfile(p); RenderPage(); }),
                Ui.Button("EXPORT", () => { var f = Ui.SaveFile("Export profile", Ui.SafeFileName(p.Name) + ".sfhudprofile", "SFHud profile (*.sfhudprofile;*.shudprofile)|*.sfhudprofile;*.shudprofile"); if (f is not null) DataStore.ExportJson(f, "profile", p); }),
                Ui.Button("DELETE", () =>
                {
                    if (DataStore.Data.Profiles.Count <= 1 || active) { Ui.Info(this, "Profiles", active ? "Switch to another profile before deleting this one." : "You need at least one profile."); return; }
                    if (!Ui.Confirm(this, "Delete profile", $"Delete the \"{p.Name}\" profile with all its presets, pads and keybinds? A backup is made first.")) return;
                    DataStore.Backup("profile-deleted");
                    DataStore.Data.Profiles.Remove(p);
                    DataStore.MarkDirty();
                    RenderPage();
                }, "Sf.Button.Small.Danger"));
            DockPanel.SetDock(acts, Dock.Right);
            row.Children.Add(acts);
            row.Children.Add(Ui.Stack(Ui.Text(p.Name + (active ? "   ● ACTIVE" : ""), 14, active ? "B.Accent" : "B.Text"), Ui.Label($"{p.Presets.Count} PRESETS · {p.Keybinds.Count} CUSTOM KEYS · CREATED {p.Created:MMM d, yyyy}")));
            profiles.Children.Add(row);
        }
        sp.Children.Add(Ui.Spacer(8));
        sp.Children.Add(Card("PERSONAL PROFILES", "For example Main RP, Training, Event, Casual. Each has its own settings, keybinds, presets, layouts and notes.", profiles,
            Ui.Wrap(
                Ui.ProFade(Ui.Button("+ NEW PROFILE", () =>
                {
                    if (!Edition.CanAddProfile(DataStore.Data.Profiles.Count)) { Ui.RequirePro(ProFeature.UnlimitedProfiles); return; }
                    if (Ui.Prompt(this, "New profile", DataStore.UniqueName("Training", DataStore.Data.Profiles.Select(x => x.Name)), "Starts with the built-in presets. Use DUPLICATE to copy an existing profile instead.") is not { Length: > 0 } n) return;
                    DataStore.Data.Profiles.Add(DataStore.CreateProfile(n.Trim()));
                    DataStore.MarkDirty();
                    RenderPage();
                }, "Sf.Button.Small.Primary"), ProFeature.UnlimitedProfiles, !Edition.CanAddProfile(DataStore.Data.Profiles.Count)),
                Ui.Button("IMPORT…", () =>
                {
                    var f = Ui.OpenFile("Import profile", "SFHud profile (*.sfhudprofile;*.shudprofile)|*.sfhudprofile;*.shudprofile");
                    if (f is null) return;
                    try
                    {
                        if (!Edition.CanAddProfile(DataStore.Data.Profiles.Count)) { Ui.RequirePro(ProFeature.UnlimitedProfiles); return; }
                        var p = DataStore.ImportJson<UserProfile>(f, "profile") ?? throw new InvalidDataException("Empty file.");
                        p.Id = Ids.New();
                        p.Name = DataStore.UniqueName(p.Name, DataStore.Data.Profiles.Select(x => x.Name));
                        p.Presets = p.Presets.Select(DataStore.Reidentify).ToList();
                        p.ActivePresetId = p.Presets.FirstOrDefault()?.Id ?? "";
                        DataStore.Data.Profiles.Add(p);
                        DataStore.Normalize(DataStore.Data);
                        DataStore.MarkDirty();
                        RenderPage();
                    }
                    catch (Exception ex) { Ui.Info(this, "Import failed", ex.Message); }
                }))));
        return sp;
    }

    private static Border Swatch(string hex) => new() { Width = 22, Height = 14, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 4, 4, 6), Background = Ui.Brush(hex), BorderThickness = new Thickness(1), BorderBrush = ThemeManager.Brush("B.Line") };

    private void IconDialog(string current, Action<string> set)
    {
        var w = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent, SizeToContent = SizeToContent.WidthAndHeight, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        var frame = new HudFrame { Padding = new Thickness(16), Margin = new Thickness(10), Width = 380, Content = Ui.Stack(Ui.H2("ICON"), Ui.Spacer(8), Ui.IconPicker(current, g => { set(g); w.Close(); })) };
        frame.SetResourceReference(HudFrame.FillProperty, "B.Bg");
        frame.SetResourceReference(HudFrame.StrokeProperty, "B.Accent");
        w.Content = frame;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Deactivated += (_, _) => { try { w.Close(); } catch (InvalidOperationException) { } };
        w.Show();
    }

    // ================================================================ GAMES + SERVERS

    /// <summary>The apps the overlay may appear over. Everything else (desktop, browsers, Discord, SFHud itself) never gets it.</summary>
    private HudFrame WhitelistCard()
    {
        var list = new StackPanel();
        string selected = string.Join(", ", Target.Processes.Select(p => p + ".exe"));
        foreach (var p in S.OverlayApps.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
        {
            string proc = p;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var del = Ui.IconButton("", $"Remove {proc}.exe from the whitelist", () => { S.OverlayApps.RemoveAll(x => x.Equals(proc, StringComparison.OrdinalIgnoreCase)); DataStore.MarkDirty(); _overlay.Nudge(); RenderPage(); });
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var name = GameCatalog.All(S).FirstOrDefault(t => t.Processes.Contains(proc, StringComparer.OrdinalIgnoreCase))?.Name;
            row.Children.Add(Ui.Stack(Ui.Text(proc + ".exe", 12.5, "B.Text", wrap: false), Ui.Label(name ?? "custom")));
            list.Children.Add(row);
        }
        if (S.OverlayApps.Count == 0) list.Children.Add(Ui.Hint("Empty: only the selected game gets the overlay."));

        var box = Ui.Placeholder(Ui.TextBox("", _ => { }), "e.g. hl2.exe");
        var add = Ui.Button("ADD", () =>
        {
            string p = GameCatalog.Clean(box.Text);
            if (p.Length == 0) return;
            if (!S.OverlayApps.Contains(p, StringComparer.OrdinalIgnoreCase)) S.OverlayApps.Add(p);
            DataStore.MarkDirty();
            RenderPage();
        }, "Sf.Button.Small.Primary");
        var addRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(add, Dock.Right);
        add.Margin = new Thickness(6, 0, 0, 0);
        add.VerticalAlignment = VerticalAlignment.Center;
        addRow.Children.Add(add);
        addRow.Children.Add(box);

        return Card("OVERLAY WHITELIST",
            $"The overlay only appears while one of these apps is in front. The selected game ({selected}) is always allowed. " +
            "It hides over the SFHud window, the desktop and anything else. Like other anti-cheat-safe overlays, it's a separate " +
            "click-through window that follows the game. Nothing is injected into the game.",
            list, addRow,
            Ui.Wrap(Ui.Button("RESET TO BUILT-IN GAMES", () => { S.OverlayApps = GameCatalog.DefaultWhitelist(); DataStore.MarkDirty(); RenderPage(); })));
    }

    private FrameworkElement GamesPage()
    {
        var sp = new StackPanel();
        sp.Children.Add(PageHeader("Games & servers", "Which game the overlay follows, which apps are allowed to show it, and your favourite servers."));
        var targets = new StackPanel();
        foreach (var t in GameCatalog.All(S))
        {
            var tt = t;
            bool active = t.Id == S.TargetId;
            var row = new DockPanel();
            var acts = Ui.HStack();
            if (!active) acts.Children.Add(Ui.Button("USE", () => { S.TargetId = tt.Id; SessionTracker.Instance.ResetForTargetChange(); _overlay.SetTarget(Target); DataStore.MarkDirty(); RenderPage(); }, "Sf.Button.Small.Primary"));
            if (t.IsCustom) acts.Children.Add(Ui.Button("REMOVE", () => { S.CustomTargets.RemoveAll(c => c.Id == tt.Id); if (S.TargetId == tt.Id) S.TargetId = GameCatalog.BuiltIn[0].Id; DataStore.MarkDirty(); RenderPage(); }, "Sf.Button.Small.Danger"));
            DockPanel.SetDock(acts, Dock.Right);
            row.Children.Add(acts);
            acts.VerticalAlignment = VerticalAlignment.Center;
            if (active) acts.Children.Add(StatusPill("ACTIVE", "B.Ok", ""));
            var sub = Ui.Label(t.Tag + "  ·  " + t.Subtitle);
            sub.Margin = new Thickness(0, 2, 0, 0);
            row.Children.Add(Ui.Stack(Ui.Text(t.Name, 13.5, active ? "B.Accent" : "B.Text", wrap: false), sub));
            // One padded block per game; the active one is tinted so it stands out from the list.
            var block = new Border { Child = row, Padding = new Thickness(10, 7, 8, 7), Margin = new Thickness(0, 0, 0, 4), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
            block.SetResourceReference(Border.BorderBrushProperty, active ? "B.Line.Strong" : "B.Line");
            if (active) block.SetResourceReference(Border.BackgroundProperty, "B.Accent.Fill");
            targets.Children.Add(block);
        }
        var apps = new StackPanel();
        var gamesCard = Card("GAME TARGET", "Pick the game the overlay attaches to, \"Any whitelisted app\" (follows whichever whitelisted app is in front), or add a running app with SCAN.", targets,
            Ui.Button("SCAN RUNNING APPS", () =>
            {
                apps.Children.Clear();
                var list = GameLocator.ListRunningApps();
                if (list.Count == 0) apps.Children.Add(Ui.Hint("No windowed apps found."));
                foreach (var a in list)
                {
                    var app = a;
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                    var add = Ui.Button("ADD", () =>
                    {
                        var target = GameCatalog.CreateCustom(app.ProcessName, app.Title);
                        if (!GameCatalog.All(S).Any(x => x.Id == target.Id)) S.CustomTargets.Add(target);
                        if (!S.OverlayApps.Contains(app.ProcessName, StringComparer.OrdinalIgnoreCase)) S.OverlayApps.Add(app.ProcessName);
                        S.TargetId = target.Id;
                        _overlay.SetTarget(Target);
                        DataStore.MarkDirty();
                        RenderPage();
                    });
                    DockPanel.SetDock(add, Dock.Right);
                    row.Children.Add(add);
                    row.Children.Add(Ui.Stack(Ui.Text(app.Title, 12.5, "B.Text", wrap: false), Ui.Label(app.ProcessName + ".exe")));
                    apps.Children.Add(row);
                }
            }), apps);

        if (_servers.Count != S.Servers.Count) { _servers.Clear(); foreach (var f in S.Servers) _servers.Add(new ServerEntry(f)); _ = RefreshServers(); }
        var servers = new StackPanel();
        foreach (var s in _servers)
        {
            var se = s;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var acts = Ui.HStack(Ui.Button("CONNECT", () => ServerQuery.Connect(se.Address), "Sf.Button.Small.Primary"),
                Ui.Button("REMOVE", () => { S.Servers.Remove(se.Favorite); _servers.Remove(se); DataStore.MarkDirty(); RenderPage(); }, "Sf.Button.Small.Danger"));
            DockPanel.SetDock(acts, Dock.Right);
            row.Children.Add(acts);
            row.Children.Add(Ui.Stack(Ui.Text(se.Title, 13, se.Online ? "B.Text" : "B.Dim", wrap: false), Ui.Label($"{se.Address} · {se.Status} · {se.Map} · {se.Players} · {se.Ping}")));
            servers.Children.Add(row);
        }
        if (_servers.Count == 0) servers.Children.Add(Ui.Hint("No servers saved."));
        var name = Ui.Placeholder(Ui.TextBox("", _ => { }), "Optional, e.g. 501st CWRP");
        name.ToolTip = "Name (optional)";
        var addr = Ui.Placeholder(Ui.TextBox("", _ => { }), "e.g. 203.0.113.10:27015");
        addr.ToolTip = "Address, e.g. 203.0.113.10:27015";
        var err = Ui.Hint("");
        var serversCard = Card("FAVOURITE SERVERS", "Live name, map, players and ping (standard Source A2S query). SFHud only contacts addresses you add.", servers,
            Ui.Row("Name", name), Ui.Row("Address", addr), err,
            Ui.Wrap(Ui.Button("ADD SERVER", () =>
            {
                if (!ServerQuery.TryParse(addr.Text.Trim(), out string host, out int port)) { err.Text = "Enter an address like 203.0.113.10:27015 or play.example.com"; return; }
                string normalized = $"{host}:{port}";
                if (S.Servers.Any(s => string.Equals(s.Address, normalized, StringComparison.OrdinalIgnoreCase))) { err.Text = "Already in your list."; return; }
                var fav = new ServerFavorite { Name = name.Text.Trim(), Address = normalized };
                S.Servers.Add(fav);
                var entry = new ServerEntry(fav);
                _servers.Add(entry);
                DataStore.MarkDirty();
                _ = entry.RefreshAsync().ContinueWith(_ => Dispatcher.BeginInvoke(RenderPage));
                RenderPage();
            }, "Sf.Button.Small.Primary"), Ui.Button("REFRESH", () => _ = RefreshServers())));
        sp.Children.Add(Columns(380, gamesCard, WhitelistCard(), serversCard));
        return sp;
    }

    private async Task RefreshServers()
    {
        await Task.WhenAll(_servers.ToList().Select(s => s.RefreshAsync()));
        if (_page == "games") RenderPage();
    }

    // ================================================================ SETTINGS

    private FrameworkElement SettingsPage()
    {
        var s = S;
        var prof = DataStore.Profile;
        var sp = new StackPanel { MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(PageHeader("Settings", $"Saved automatically for the \"{prof.Name}\" profile. Click a section to open it."));
        void A() { DataStore.MarkDirty(); ThemeManager.Apply(); }

        // Appearance
        var themeIds = new[] { "preset" }.Concat(PresetCatalog.Themes.Select(t => t.Id)).Concat(prof.CustomThemes.Select(t => t.Id)).ToList();
        var custom = new StackPanel();
        foreach (var ct in prof.CustomThemes)
        {
            var t = ct;
            custom.Children.Add(new Expander
            {
                Header = Ui.H2("CUSTOM THEME · " + t.Name.ToUpperInvariant(), 11),
                Content = Ui.Stack(
                    Ui.Row("Name", Ui.TextBox(t.Name, v => { t.Name = v.Trim(); DataStore.MarkDirty(); })),
                    Ui.Label("BACKGROUND"), Ui.ColorPicker(t.Bg, v => { if (v.Length > 0) { t.Bg = v; A(); } }, false),
                    Ui.Label("PANELS"), Ui.ColorPicker(t.Elevated, v => { if (v.Length > 0) { t.Elevated = v; A(); } }, false),
                    Ui.Label("ACCENT"), Ui.ColorPicker(t.Accent, v => { if (v.Length > 0) { t.Accent = v; A(); } }, false),
                    Ui.Label("TEXT"), Ui.ColorPicker(t.Text, v => { if (v.Length > 0) { t.Text = v; A(); } }, false),
                    Ui.Label("SECONDARY TEXT"), Ui.ColorPicker(t.Dim, v => { if (v.Length > 0) { t.Dim = v; A(); } }, false),
                    Ui.Label("WARNING"), Ui.ColorPicker(t.Warn, v => { if (v.Length > 0) { t.Warn = v; A(); } }, false),
                    Ui.Label("ALERT"), Ui.ColorPicker(t.Alert, v => { if (v.Length > 0) { t.Alert = v; A(); } }, false),
                    Ui.Switch("Light theme", t.Light, v => { t.Light = v; A(); }),
                    Ui.Row("Corners", Ui.Combo(Enum.GetValues<HudCorners>(), t.Corners, v => { t.Corners = v; A(); })),
                    Ui.Button("DELETE THEME", () => { prof.CustomThemes.Remove(t); if (s.Theme == t.Id) s.Theme = "preset"; A(); RenderPage(); }, "Sf.Button.Small.Danger")),
            });
        }
        var fonts = ElementEditor.FontList;
        sp.Children.Add(Ui.Section("APPEARANCE", "Theme, colours, background, corners, fonts and scaling", _subTab == "appearance",
            Ui.Row("Theme", Ui.Combo(themeIds, s.Theme, v => { s.Theme = v; A(); }, id => id == "preset" ? $"Follow server preset ({PresetCatalog.Theme(DataStore.Preset.ThemeId)?.Name ?? DataStore.Preset.ThemeId})" : prof.CustomThemes.FirstOrDefault(t => t.Id == id)?.Name ?? PresetCatalog.Theme(id)?.Name ?? id),
                "Dark, Light, Midnight, Dark red, Blue, Purple, the preset styles, or your own."),
            custom,
            Ui.ProFade(Ui.Button("+ CUSTOM THEME FROM CURRENT", () =>
            {
                if (!Ui.RequirePro(ProFeature.CustomThemes)) return;
                var t = DataStore.DeepClone(ThemeManager.Current);
                t.Id = Ids.New();
                t.Name = DataStore.UniqueName("My theme", prof.CustomThemes.Select(x => x.Name));
                prof.CustomThemes.Add(t);
                s.Theme = t.Id;
                A();
                _subTab = "appearance";
                RenderPage();
            }), ProFeature.CustomThemes),
            Ui.Label("ACCENT OVERRIDE"),
            Ui.ColorPicker(s.AccentOverride, v => { s.AccentOverride = v; A(); }),
            Ui.Row("Corners", Ui.Combo(new[] { "", "Chamfer", "Rounded", "Square" }, s.Corners, v => { s.Corners = v; A(); }, v => v.Length == 0 ? "Theme default" : v)),
            Ui.Slider("Corner radius (rounded)", 0, 20, s.CornerRadius, v => $"{v:0}", v => { s.CornerRadius = Math.Round(v); A(); }, step: 1),
            Ui.Row("Heading font", Ui.Combo(fonts, s.DisplayFont, v => { s.DisplayFont = v; A(); }, f => f.Length == 0 ? "Theme font" : f)),
            Ui.Row("Body font", Ui.Combo(fonts, s.BodyFont, v => { s.BodyFont = v; A(); }, f => f.Length == 0 ? "Theme font" : f)),
            Ui.Slider("Window text scale", 0.85, 1.4, s.FontScale, v => $"{v:0.00}×", v => { s.FontScale = Math.Round(v, 2); A(); }, "Scales this window. The overlay has its own scale (Overlay › Display)."),
            Ui.Switch("Auto-scale on large screens", s.AutoScaleLargeWindows, v => { s.AutoScaleLargeWindows = v; A(); }, "Grows this window's text and cards on big windows (1440p, ultrawide, 4K at 100%). 1080p and 4:3 keep the normal size."),
            Ui.Slider("Window opacity", 0.6, 1, s.WindowOpacity, v => $"{v:P0}", v => { s.WindowOpacity = Math.Round(v, 2); A(); }),
            Ui.Row("Background image", Ui.Wrap(
                Ui.ProFade(Ui.Button("CHOOSE…", () => { if (!Ui.RequirePro(ProFeature.CustomThemes)) return; var f = Ui.OpenFile("Background image", "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"); if (f is not null) { s.BackgroundImage = f; A(); } }), ProFeature.CustomThemes),
                Ui.Button("CLEAR", () => { s.BackgroundImage = ""; A(); }))),
            Ui.Slider("Background image strength", 0.02, 0.6, s.BackgroundImageOpacity, v => $"{v:P0}", v => { s.BackgroundImageOpacity = Math.Round(v, 2); A(); }),
            Ui.Row("Icons", Ui.Combo(new[] { "fluent", "minimal" }, s.IconPack, v => { s.IconPack = v; A(); }, v => v == "fluent" ? "Fluent (Windows icons)" : "Minimal (no sidebar icons)")),
            Ui.Switch("Animations", !s.ReducedMotion, v => { s.ReducedMotion = !v; A(); }, "Off = no fades or flashes anywhere."),
            Ui.Switch("Scanline texture", s.Scanlines, v => { s.Scanlines = v; A(); }),
            Ui.Switch("Compact mode (icon-only sidebar)", s.CompactMode, v => { s.CompactMode = v; A(); })));

        // Notifications
        sp.Children.Add(Ui.Section("NOTIFICATIONS & SOUNDS", "Toasts, timer beeps, alert sounds", _subTab == "sounds",
            Ui.Switch("Show toasts on the overlay", s.Toasts, v => { s.Toasts = v; DataStore.MarkDirty(); }),
            Ui.Slider("Toast duration", 2, 15, s.ToastSeconds, v => $"{v:0.#} s", v => { s.ToastSeconds = Math.Round(v * 2) / 2; DataStore.MarkDirty(); }, step: 0.5),
            Ui.Row("Timer done", SoundCombo(s.TimerSound, v => { s.TimerSound = v; DataStore.MarkDirty(); }, false)),
            Ui.Row("Notifications", SoundCombo(s.NotifySound, v => { s.NotifySound = v; DataStore.MarkDirty(); }, false)),
            Ui.Row("Alerts / DEFCON up", SoundCombo(s.AlertSound, v => { s.AlertSound = v; DataStore.MarkDirty(); }, false)),
            Ui.Slider("Volume", 0, 1, s.SoundVolume, v => $"{v:P0}", v => { s.SoundVolume = Math.Round(v, 2); DataStore.MarkDirty(); }),
            Ui.Switch("Mute all SFHud sounds", s.SoundMuted, v => { s.SoundMuted = v; DataStore.MarkDirty(); }),
            Ui.Button("MANAGE SOUNDS (TIMERS PAGE)", () => Navigate("timers"))));

        // Music
        var sources = new StackPanel();
        foreach (var src in MediaService.Instance.SourcesQuick())
        {
            var x = src;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            bool pinned = s.MediaSessionId == x.Id;
            var use = Ui.Button(pinned ? "PINNED" : "PIN", () => { MediaService.Instance.PinSession(x.Id); _overlay.Rebuild(ElementKind.Media); RenderPage(); }, pinned ? "Sf.Button.Small.Primary" : "Sf.Button.Small", "Follow only this player");
            use.Margin = new Thickness(6, 0, 0, 4); use.VerticalAlignment = VerticalAlignment.Center; // in line with its row
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            var label = Ui.Text($"{(x.Playing ? "●  " : "○  ")}{x.App}{(x.Playing ? "  ·  playing" : "  ·  paused")}", 12.5, x.Playing ? "B.Ok" : "B.Text", wrap: false);
            // Unidentified players (e.g. the Steam overlay browser, which has no window) can still be pinned by their id.
            label.ToolTip = "Windows id: " + x.Id + (x.App == "Unknown player" ? "\nIf this is the Steam overlay browser, press PIN to follow it." : "");
            row.Children.Add(label);
            sources.Children.Add(row);
        }
        if (sources.Children.Count == 0) sources.Children.Add(Ui.Hint("Nothing is playing anywhere right now. Start a video or song, then refresh."));
        string tip = s.MediaMode switch
        {
            MediaMode.SteamBrowser => "Steam browser: open the Steam overlay (Shift+Tab) in game, play music in its web browser, then use the player or its key.",
            MediaMode.Auto => "Auto follows whichever player is playing (Steam browser, any browser tab, Spotify…). Pick one source if two players fight.",
            MediaMode.Firefox => "Firefox / LibreWolf: media keys must be on (about:config → media.hardwaremediakeys.enabled = true, the default).",
            _ => "Chromium browsers (Chrome, Brave, Edge, Opera, Vivaldi, Arc…): any tab playing audio or video shows up. If nothing appears, check the browser's \"Hardware Media Key Handling\" flag is on (it is by default).",
        };
        sp.Children.Add(Ui.Section("MUSIC PLAYER", "Now Playing for the Steam browser, Chrome, Brave, Edge and other Chromium browsers, Firefox and Spotify", _subTab == "music",
            Ui.Hint("SFHud uses Windows' media controls (the same ones as your keyboard's play key and the volume flyout). It doesn't inject into Steam, the game or your browser."),
            Ui.Row("Listen to", Ui.Combo(MediaService.Choices, s.MediaSessionId.Length > 0 ? MediaMode.AnyPlayer : s.MediaMode, v => { MediaService.Instance.SetMode(v); _overlay.Rebuild(ElementKind.Media); RenderPage(); },
                v => MediaService.Describe(v) + (v != MediaMode.Auto && MediaService.Instance.IsModePlaying(v) ? "   ●" : ""))),
            s.MediaSessionId.Length > 0 ? Ui.Hint($"Pinned to one player ({MediaService.Friendly(s.MediaSessionId)}). Pick a source above to unpin.") : Ui.Spacer(0),
            Ui.Hint(tip),
            Ui.Switch("Show the player on the overlay", s.MediaVisible, v => _overlay.SetMusicVisible(v), $"It appears only in game, on every overlay page. Also toggled from the Alt+~ menu (MUSIC) or {Gesture("media.toggleui")}."),
            Ui.Switch("Pause overlay videos while you're out of the game", s.VideoPauseOutOfGame, v => { s.VideoPauseOutOfGame = v; DataStore.MarkDirty(); }, "They resume when you go back in. Add a Video element in customize mode."),
            Ui.Label("PLAYING NOW"),
            sources,
            Ui.Wrap(Ui.Button("REFRESH", () => { MediaService.Instance.Pick(); _subTab = "music"; RenderPage(); }),
                    Ui.Button("TEST PLAY / PAUSE", () => _ = MediaService.Instance.PlayPauseAsync(), "Sf.Button.Small.Primary"))));

        // Behaviour
        sp.Children.Add(Ui.Section("BEHAVIOUR", "Confirmations, startup, quick timer", _subTab == "behaviour",
            Ui.Switch("Ask before deleting or clearing", s.ConfirmDeletes, v => { s.ConfirmDeletes = v; DataStore.MarkDirty(); }, "Pads also have their own setting. Deleted notes can always be undone from the pad toolbar."),
            Ui.Switch("Show the overlay when SFHud starts", s.AutoEngage, v => { s.AutoEngage = v; DataStore.MarkDirty(); }),
            Ui.Switch("Start minimized", s.StartMinimized, v => { s.StartMinimized = v; DataStore.MarkDirty(); }),
            Ui.Slider("Quick timer (Ctrl+T) length", 1, 60, s.QuickTimerMinutes, v => $"{v:0} min", v => { s.QuickTimerMinutes = (int)v; DataStore.MarkDirty(); }, step: 1)));

        // Intel
        sp.Children.Add(Ui.Section("SERVER INTEL (CONSOLE.LOG)", "Alerts and server name from Garry's Mod's console log", _subTab == "intel",
            Ui.Switch("Read console.log", s.IntelEnabled, v => { s.IntelEnabled = v; if (v) IntelService.Instance.Start(); else IntelService.Instance.Stop(); DataStore.MarkDirty(); }, "Read-only. GMod writes it when launched with -condebug (LAUNCH GAME on Home does that)."),
            Ui.Row("console.log path", Ui.TextBox(s.ConsoleLogPath, v => { v = v.Trim().Trim('"'); if (v == s.ConsoleLogPath) return; s.ConsoleLogPath = v; DataStore.MarkDirty(); IntelService.Instance.Restart(); }), "Empty = find it through Steam's library folders."),
            Ui.Label(IntelService.Instance.LogPath is { } lp ? "USING  " + lp : "NOT FOUND · START GMOD WITH -condebug, OR PASTE THE PATH"),
            Ui.Label("FEED  " + IntelService.Instance.FeedStatus)));


        // Data
        var backups = new StackPanel();
        foreach (var b in DataStore.Backups().Take(8))
        {
            var bf = b;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var restore = Ui.Button("RESTORE", () =>
            {
                if (!Ui.Confirm(this, "Restore backup", $"Replace all your current data with the backup from {bf.LastWriteTime:MMM d, HH:mm}? Your current data is backed up first.", "RESTORE")) return;
                if (DataStore.RestoreBackup(bf.FullName)) { ThemeManager.Apply(); ShowBanner("Backup restored."); }
                else ShowBanner("That backup couldn't be read.");
                RenderPage();
            });
            DockPanel.SetDock(restore, Dock.Right);
            row.Children.Add(restore);
            row.Children.Add(Ui.Label($"{bf.LastWriteTime:MMM d  HH:mm}   ·   {bf.Length / 1024.0:0} KB   ·   {bf.Name.Split('-').Last().Replace(".json", "")}"));
            backups.Children.Add(row);
        }
        sp.Children.Add(Ui.Section("DATA & BACKUPS", "Save, back up, restore, import and export", _subTab == "data",
            Ui.Hint($"Everything is in {SettingsStore.DataDir}. SFHud saves a moment after every change, keeps rolling backups (at start-up, every 30 minutes and before anything destructive), and recovers from a damaged file automatically."),
            Ui.Wrap(
                Ui.Button("SAVE NOW", () => { DataStore.SaveNow(); RenderPage(); }, "Sf.Button.Small.Primary", "Ctrl+S"),
                Ui.Button("BACK UP NOW", () => { DataStore.SaveNow(); DataStore.Backup("manual"); RenderPage(); }),
                Ui.Button("OPEN DATA FOLDER", () => OpenUrl(SettingsStore.DataDir)),
                Ui.Button("OPEN LOGS", () => OpenUrl(SettingsStore.LogDir))),
            Ui.Wrap(
                Ui.Button("EXPORT EVERYTHING…", () => { var f = Ui.SaveFile("Export all SFHud data", $"SFHud-backup-{DateTime.Now:yyyyMMdd}.json", "SFHud data (*.json)|*.json"); if (f is not null) { DataStore.SaveNow(); DataStore.ExportAll(f); } }),
                Ui.Button("IMPORT EVERYTHING…", () =>
                {
                    var f = Ui.OpenFile("Import SFHud data", "SFHud data (*.json)|*.json");
                    if (f is null) return;
                    if (!Ui.Confirm(this, "Import", "Replace all current profiles, presets and notes with this file? Your current data is backed up first.", "IMPORT")) return;
                    if (DataStore.ImportAll(f)) { ThemeManager.Apply(); ShowBanner("Imported."); } else ShowBanner("That file couldn't be read as SFHud data.");
                    RenderPage();
                })),
            Ui.Slider("Backups to keep", 5, 100, s.BackupKeep, v => $"{v:0}", v => { s.BackupKeep = (int)v; DataStore.MarkDirty(); }, step: 1),
            Ui.Label("RECENT BACKUPS"),
            backups));

        // About
        string changelog = Installer.ReadResource("SFHud.CHANGELOG.md");
        sp.Children.Add(Ui.Section("ABOUT", $"SFHud {AppInfo.Version}", _subTab == "about",
            Ui.Text($"SFHud {AppInfo.Version} · a lightweight overlay and note pad companion for Garry's Mod roleplay.", 13),
            Ui.Hint("Runs locally. Contacts only addresses you add (servers, status URL, local AI download from Hugging Face / GitHub when you click Install)."),
            Ui.Wrap(Ui.Button(Edition.Unlocked ? "★ SFHUD PRO · LICENSE" : "★ SFHUD PRO", () => ShowUpgrade(null), "Sf.Button.Small.Primary"),
                    Ui.Button("INSTALL / UPDATE SFHUD…", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--install") { UseShellExecute = false }); } catch (Exception ex) { ShowBanner(ex.Message); } }, tip: "Installs this copy with shortcuts and an uninstall entry.")),
            changelog.Length > 0 ? new Expander { Header = Ui.H2("CHANGELOG", 11), Content = NoteMarkup.Render(changelog.Length > 6000 ? changelog[..6000] + "\n…" : changelog, new MarkupOptions { FontSize = 12.5, Foreground = ThemeManager.Brush("B.Text"), Accent = ThemeManager.Brush("B.Accent"), Dim = ThemeManager.Brush("B.Dim") }) } : Ui.Spacer(0)));
        // Keep sections open across refreshes (a deep link via _subTab opens one too).
        foreach (var ex in sp.Children.OfType<Expander>())
        {
            string key = ((ex.Header as StackPanel)?.Children[0] as TextBlock)?.Text ?? "";
            if (ex.IsExpanded) _openSections.Add(key);
            if (_openSections.Contains(key)) ex.IsExpanded = true;
            ex.Expanded += (_, _) => _openSections.Add(key);
            ex.Collapsed += (_, _) => _openSections.Remove(key);
        }
        _subTab = "";
        return sp;
    }

    // ================================================================ global search

    private sealed record SearchHit(string Kind, string Title, string Detail, Action Go);

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchResults.Children.Clear();
        if (q.Length < 2) { SearchPopup.IsOpen = false; return; }
        var hits = SearchAll(q).Take(40).ToList();
        if (hits.Count == 0) SearchResults.Children.Add(Ui.Hint("Nothing found."));
        foreach (var h in hits)
        {
            var hit = h;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var kind = Ui.Label(h.Kind.ToUpperInvariant(), 9.5);
            kind.Width = 74;
            row.Children.Add(kind);
            row.Children.Add(Ui.Stack(Ui.Text(h.Title, 12.5, "B.Text", wrap: false), h.Detail.Length > 0 ? Ui.Label(h.Detail, 10) : Ui.Spacer(0)));
            var b = new Button { Content = row, Style = Ui.Style("Sf.IconButton"), HorizontalContentAlignment = HorizontalAlignment.Left, Height = double.NaN, Padding = new Thickness(8, 5, 8, 5), FontFamily = (System.Windows.Media.FontFamily)FindResource("F.Body") };
            b.Click += (_, _) => { SearchPopup.IsOpen = false; hit.Go(); };
            SearchResults.Children.Add(b);
        }
        SearchPopup.IsOpen = true;
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Text = ""; SearchPopup.IsOpen = false; e.Handled = true; }
        else if (e.Key == Key.Enter && SearchResults.Children.OfType<Button>().FirstOrDefault() is { } first) { first.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); e.Handled = true; }
    }

    private IEnumerable<SearchHit> SearchAll(string q)
    {
        bool M(string s) => s.Contains(q, StringComparison.OrdinalIgnoreCase);
        var settings = new (string Title, string Page, string Sub, string Keywords)[]
        {
            ("Theme & colours", "settings", "appearance", "theme dark light midnight red blue purple accent colour color custom background font corners rounded scale opacity icons animation compact"),
            ("Notification sounds", "settings", "sounds", "sound beep volume mute toast notification duration"),
            ("Music player (Steam browser)", "settings", "music", "music media steam browser chrome brave play pause spotify"),
            ("Behaviour", "settings", "behaviour", "confirm delete start minimized auto engage"),
            ("Server intel / console.log", "settings", "intel", "console log condebug intel alerts chat"),
            ("Data & backups", "settings", "data", "save backup restore import export data corrupt"),
            ("About / changelog", "settings", "about", "about version changelog pro license install update"),
            ("Keybinds", "overlay", "keys", "keybind hotkey shortcut key combo conflict"),
            ("Crosshair", "overlay", "crosshair", "crosshair reticle dot cross"),
            ("Overlay display", "overlay", "display", "overlay opacity scale resolution distraction grid guides lock toast"),
            ("Overlay pages & elements", "overlay", "pages", "page element layout card hud"),
            ("DEFCON levels & detection", "defcon", "", "defcon level detection ocr screen status url"),
            ("Timer presets & sounds", "timers", "", "timer stopwatch preset countdown sound pixabay"),
            ("Local AI", "ai", "", "ai model qwen llm summarize ask lm studio ollama"),
            ("SFHud Pro", "pro", "", "pro upgrade buy purchase license key activate unlock"),
            ("Presets & profiles", "presets", "", "preset profile server swrp zombie military general import export"),
            ("Games & servers", "games", "", "game target gmod server favourite connect"),
        };
        foreach (var s in settings.Where(s => M(s.Title) || M(s.Keywords)))
            yield return new SearchHit("Setting", s.Title, "", () => { _subTab = s.Sub; Navigate(s.Page); });
        foreach (var r in App.Keys?.Rows.Where(r => M(r.Name) || M(r.Gesture)).Take(8) ?? Enumerable.Empty<BindRow>())
            yield return new SearchHit("Keybind", r.Name, r.GestureText, () => { _subTab = "keys"; _keyFilter = r.Name; Navigate("overlay"); });
        foreach (var p in DataStore.Preset.Pages.Where(p => M(p.Name)))
            yield return new SearchHit("Page", p.Name, "overlay page", () => { _subTab = "pages"; _overlayPageId = p.Id; Navigate("overlay"); });
        foreach (var p in DataStore.Profile.Presets.Where(p => M(p.Name)))
            yield return new SearchHit("Preset", p.Name, p.Id == DataStore.Preset.Id ? "active" : "click to manage", () => Navigate("presets"));
        foreach (var p in DataStore.Preset.Pads.Where(p => M(p.Title)))
            yield return new SearchHit("Pad", p.Title, $"{p.Notes.Count} note{(p.Notes.Count == 1 ? "" : "s")}", () => { Pads.Select(p); Navigate("notes"); });
        foreach (var h in Pads.Search(q, allPresets: true).Take(25))
        {
            var pre = DataStore.Profile.Presets.First(x => x.Pads.Contains(h.Pad));
            yield return new SearchHit("Note", h.Note.FirstLine.Length > 70 ? h.Note.FirstLine[..70] + "…" : h.Note.FirstLine, $"{pre.Name} › {h.Pad.Title} · {h.Note.Time}", () =>
            {
                if (pre.Id != DataStore.Preset.Id) DataStore.SwitchPreset(pre);
                Pads.Select(h.Pad);
                Navigate("notes");
            });
        }
    }
}
