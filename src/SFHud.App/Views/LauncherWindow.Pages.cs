using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SFHud.Controls;
using SFHud.Interop;
using SFHud.Services;

namespace SFHud.Views;

public partial class LauncherWindow
{
    private string _subTab = "";
    private string _overlayPageId = "";
    private string _overlayElementId = "";
    private TextBlock? _liveGame, _liveSession, _liveTotal;

    // ================================================================ shared page helpers

    /// <summary>"03 // OVERLAY" above each page title (set before a page is built).</summary>
    private static string Eyebrow = "";

    private static FrameworkElement PageHeader(string title, string subtitle, params UIElement[] right)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (right.Length > 0)
        {
            var r = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var x in right) r.Children.Add(x);
            DockPanel.SetDock(r, Dock.Right);
            d.Children.Add(r);
        }
        var sp = new StackPanel();
        var eyebrow = new TextBlock { Text = Eyebrow, Style = Ui.Style("T.Eyebrow"), Margin = new Thickness(0, 0, 0, 6) };
        sp.Children.Add(eyebrow);
        if (Ui.EmojiFor(title.ToUpperInvariant()) is { Length: > 0 } em)
        {
            var ic = Ui.Emoji(em, 19);
            ic.Margin = new Thickness(0, 0, 10, 0);
            ic.SetResourceReference(TextBlock.ForegroundProperty, "B.Accent");
            sp.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { ic, Ui.H1(title) } });
        }
        else sp.Children.Add(Ui.H1(title));
        if (subtitle.Length > 0) { var s = Ui.Text(subtitle, 12.5, "B.Dim"); s.Margin = new Thickness(0, 4, 0, 0); sp.Children.Add(s); }
        d.Children.Add(sp);
        return d;
    }

    private static HudFrame Card(string title, string? hint, params UIElement[] content)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.H2(title));
        if (hint is { Length: > 0 }) sp.Children.Add(Ui.Hint(hint));
        else sp.Children.Add(Ui.Spacer(8));
        foreach (var c in content) sp.Children.Add(c);
        return new HudFrame { Style = Ui.Style("Sf.Panel"), Content = sp, Margin = new Thickness(0, 0, 14, 14), VerticalAlignment = VerticalAlignment.Top, ShowTab = true };
    }

    private const int MaxColumns = 4;

    /// <summary>Cards in as many columns as fit (responsive to window width).</summary>
    private static FrameworkElement Columns(double minWidth, params UIElement[] items)
    {
        // Masonry-style: N star columns, cards dealt left to right, so tall and short cards don't leave gaps.
        var grid = new Grid();
        int current = 0;
        void Layout(int cols)
        {
            if (cols == current) return;
            current = cols;
            foreach (var c in grid.Children.OfType<StackPanel>()) c.Children.Clear();
            grid.Children.Clear();
            grid.ColumnDefinitions.Clear();
            var stacks = new StackPanel[cols];
            for (int i = 0; i < cols; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                stacks[i] = new StackPanel();
                Grid.SetColumn(stacks[i], i);
                grid.Children.Add(stacks[i]);
            }
            for (int i = 0; i < items.Length; i++) stacks[i % cols].Children.Add(items[i]);
        }
        Layout(1);
        // Cap at 4: beyond that, cards become thin strips on ultrawide / 4K windows.
        grid.SizeChanged += (_, e) => Layout(Math.Clamp((int)(e.NewSize.Width / minWidth), 1, Math.Min(items.Length, MaxColumns)));
        return grid;
    }

    private static FrameworkElement SubTabs(string current, Action<string> pick, params (string Id, string Label)[] tabs)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var (id, label) in tabs)
        {
            var rb = new RadioButton { Content = Ui.TabContent(label), Style = Ui.Style("Sf.Tab"), IsChecked = current == id, GroupName = "Sub" + tabs[0].Id };
            string i = id;
            rb.Checked += (_, _) => pick(i);
            w.Children.Add(rb);
        }
        var line = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 14) };
        line.SetResourceReference(Border.BackgroundProperty, "B.Line");
        return Ui.Stack(w, line);
    }

    // ================================================================ HOME

    private FrameworkElement HomePage()
    {
        var target = Target;
        var session = SessionTracker.Instance;
        var sp = new StackPanel();
        sp.Children.Add(PageHeader("SFHud", $"Home  ·  {DataStore.Preset.Name}  ·  {DataStore.Profile.Name} profile",
            Ui.Button(_overlay.Engaged ? "HIDE OVERLAY" : "SHOW OVERLAY", () => { Run("overlay.toggle"); RenderPage(); }, "Sf.Button.Small.Primary", Gesture("overlay.toggle")),
            Ui.Button("CUSTOMIZE IN GAME", () => _overlay.SetCustomize(true), tip: $"Opens the overlay menu ({Gesture("overlay.customize")})"),
            Ui.Button("NOTE PADS", () => OpenPads(null), tip: Gesture("pad.window")),
            Ui.ProFade(Ui.Button("COMPANION WINDOW", OpenCompanion, tip: "The same overlay page in a normal window, for exclusive fullscreen or a second monitor."), ProFeature.Companion)));

        // Game
        _liveGame = Ui.Text("", 13);
        _liveSession = StatValue();
        _liveTotal = StatValue();
        var stats = new Grid { Margin = new Thickness(0, 8, 0, 10) };
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var sCol = Ui.Stack(Ui.Label("SESSION"), _liveSession);
        var tCol = Ui.Stack(Ui.Label("TOTAL PLAYED"), _liveTotal);
        Grid.SetColumn(tCol, 1);
        stats.Children.Add(sCol);
        stats.Children.Add(tCol);
        var game = Card("GAME", null,
            Ui.Text(target.Name, 18, "B.Text"),
            Ui.Label(target.Tag + "  ·  " + target.Subtitle),
            Ui.Spacer(8), _liveGame, stats,
            Ui.Wrap(
                target.CanLaunch ? Ui.Button("LAUNCH GAME", LaunchGame, "Sf.Button.Small.Primary", target.SteamAppId == 4000 ? "Starts Garry's Mod through Steam with -condebug (feeds alerts / DEFCON from console.log)" : "Start through Steam") : Ui.Spacer(0),
                Ui.Button("CHANGE GAME", () => Navigate("games"))));
        UpdateHomeLive();

        // DEFCON
        var defcon = DefconCard();

        // Music
        var m = MediaService.Instance;
        Button? sourceBtn = null;
        sourceBtn = Ui.Button("SOURCE ▾", () => MediaSourceMenu.Open(sourceBtn!, RefreshPageSoon, () => { _subTab = "music"; Navigate("settings"); }),
            tip: "Choose what Now Playing listens to: Auto, Steam browser, Chrome, Brave, Edge, other Chromium browsers, Firefox, Spotify…");
        UIElement elsewhere = m.PlayingElsewhere.Length > 0 && m.PlayingElsewhereMode is { } em
            ? (UIElement)Ui.Wrap(new TextBlock { Text = $"{m.PlayingElsewhere} is playing.", FontSize = 12, Margin = new Thickness(0, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeManager.Brush("B.Amber") },
                      Ui.Button($"LISTEN TO {m.PlayingElsewhere.ToUpperInvariant()}", () => { m.SetMode(em); RefreshPageSoon(); }, "Sf.Button.Small.Primary"))
            : Ui.Spacer(0);
        var music = Card("NOW PLAYING · " + m.ModeLabel, $"Listening to {MediaService.Describe(S.MediaSessionId.Length > 0 ? MediaMode.AnyPlayer : S.MediaMode)}{(S.MediaSessionId.Length > 0 ? " (one pinned player)" : "")}. Change it with SOURCE.",
            Ui.Text(m.HasSession && m.Title.Length > 0 ? m.Title : m.Status, 14, m.HasSession ? "B.Text" : "B.Dim"),
            // The title already says which app; one readable line for artist and (if so) "paused".
            string.Join("  ·  ", new[] { m.Artist, m.HasSession && !m.Playing ? "paused" : "" }.Where(x => x.Length > 0)) is { Length: > 0 } sub
                ? Ui.Text(sub, 12, "B.Dim", wrap: false) : Ui.Spacer(0),
            elsewhere,
            SourceChips(),
            Ui.Spacer(4),
            Ui.Wrap(
                Ui.IconButton("", "Previous", () => _ = m.PrevAsync()),
                Ui.Button(m.Playing ? "PAUSE" : "PLAY", () => _ = m.PlayPauseAsync(), "Sf.Button.Small.Primary", Gesture("media.playpause")),
                Ui.IconButton("", "Next", () => _ = m.NextAsync()),
                sourceBtn,
                Ui.Button(S.MediaVisible ? "HIDE ON OVERLAY" : "SHOW ON OVERLAY", () => { Run("media.toggleui"); RenderPage(); }, tip: Gesture("media.toggleui"))));

        // Timers
        var timers = Card("TIMERS", $"{Gesture("timer.quick")} quick timer · {Gesture("timer.manual")} type one", TimerList(), Ui.Spacer(6), TimerEntry(), PresetChips());

        // Recent pads + quick add
        var recent = Pads.Recent().Take(5).ToList();
        if (recent.Count == 0) recent = Pads.Visible().Take(5).ToList();
        var padsList = new StackPanel();
        foreach (var p in recent)
        {
            var pp = p;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var open = Ui.Button("OPEN", () => OpenPads(pp));
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
            var ic = Ui.Icon(p.Icon, 13);
            ic.Foreground = ThemeManager.BrushOr(p.Color, "B.Accent");
            ic.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(ic);
            row.Children.Add(Ui.Text($"{p.Title}  ·  {p.Notes.Count} note{(p.Notes.Count == 1 ? "" : "s")}", 12.5, "B.Text", wrap: false));
            padsList.Children.Add(row);
        }
        var quick = Ui.Placeholder(Ui.TextBox("", _ => { }), $"Add a note to \"{Pads.Current.Title}\" and press Enter");
        quick.ToolTip = $"Type a note for \"{Pads.Current.Title}\" and press Enter";
        quick.KeyDown += (_, e) => { if (e.Key == Key.Enter && quick.Text.Trim().Length > 0) { Pads.Add(Pads.Current, quick.Text.Trim()); quick.Text = ""; e.Handled = true; } };
        var pads = Card("NOTE PADS", $"Quick add to \"{Pads.Current.Title}\" ({Gesture("note.quick")} in game)", quick, padsList);

        // Status
        var keys = App.Keys;
        var ai = LocalAi.Instance;
        var intel = IntelService.Instance;
        var backups = DataStore.Backups().Count();
        var status = Card("STATUS", null,
            StatusRow("Keybinds", keys is null ? "—" : keys.ProblemCount > 0 ? $"{keys.ArmedCount} ready · {keys.ProblemCount} need attention" : $"{keys.ArmedCount} ready", keys?.ProblemCount > 0 ? "B.Amber" : "B.Ok", () => { _subTab = "keys"; Navigate("overlay"); }),
            StatusRow("Overlay", _overlay.State, _overlay.Engaged ? "B.Ok" : "B.Muted", null),
            StatusRow("Intel feed", intel.FeedStatus, intel.FeedStatus.StartsWith("LIVE") ? "B.Ok" : "B.Muted", () => Navigate("settings")),
            StatusRow("Local AI", ai.StatusText, ai.IsRunning ? "B.Ok" : ai.CanRun ? "B.Accent" : "B.Muted", () => Navigate("ai")),
            StatusRow("Data", $"{(DataStore.Dirty ? "saving" : "saved")} · {backups} backups", "B.Ok", () => { _subTab = "data"; Navigate("settings"); }));

        sp.Children.Add(Columns(330, game, defcon, music, timers, pads, status));
        return sp;
    }

    /// <summary>Gives a label breathing room above / below (section sub-headings, info lines between controls).</summary>
    private static T Spaced<T>(T e, double top, double bottom) where T : FrameworkElement
    {
        e.Margin = new Thickness(e.Margin.Left, top, e.Margin.Right, bottom);
        return e;
    }

    /// <summary>A file path that wraps at its separators instead of being cut off with "…".</summary>
    private static TextBlock PathLabel(string path)
    {
        var t = Ui.Label(path.Replace("\\", "\\​"));   // zero-width space after each \ = allowed line break
        t.TextTrimming = TextTrimming.None;
        t.TextWrapping = TextWrapping.Wrap;
        t.ToolTip = path;
        return t;
    }

    /// <summary>One-click "listen to" chips for the common sources (the full list is under SOURCE ▾).</summary>
    private FrameworkElement SourceChips()
    {
        var m = MediaService.Instance;
        var w = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        w.Children.Add(Ui.Label("LISTEN TO  "));
        ((TextBlock)w.Children[0]).VerticalAlignment = VerticalAlignment.Center;
        foreach (var (mode, label) in new[] { (MediaMode.Auto, "AUTO"), (MediaMode.SteamBrowser, "STEAM"), (MediaMode.Chrome, "CHROME"), (MediaMode.Brave, "BRAVE"), (MediaMode.Edge, "EDGE"), (MediaMode.Spotify, "SPOTIFY") })
        {
            bool sel = S.MediaSessionId.Length == 0 && S.MediaMode == mode;
            bool playing = mode != MediaMode.Auto && m.IsModePlaying(mode);
            var chip = new ToggleButton { Content = label + (playing ? " ●" : ""), Style = Ui.Style("Sf.Chip"), IsChecked = sel, Margin = new Thickness(0, 0, 5, 5),
                                          ToolTip = MediaService.Describe(mode) + (playing ? " · playing now" : "") };
            var mm = mode;
            chip.Click += (_, _) => { m.SetMode(mm); _overlay.Rebuild(ElementKind.Media); RefreshPageSoon(); };
            w.Children.Add(chip);
        }
        return w;
    }

    /// <summary>Small outlined tag ("ASSUMED", "AUTO-DETECTED"…) with its explanation underneath.</summary>
    private static FrameworkElement StatusPill(string label, string brush, string detail)
    {
        var tag = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 1, 6, 1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 3) };
        tag.SetResourceReference(Border.BorderBrushProperty, brush);
        var t = Ui.Label(label, 9.5);
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        t.FontWeight = FontWeights.Bold;
        tag.Child = t;
        if (detail.Length == 0) { tag.Margin = new Thickness(0, 0, 6, 0); return tag; }
        return Ui.Stack(tag, Ui.Text(detail, 11.5, "B.Dim"));
    }

    /// <summary>Big mono number under a small label (session / total played).</summary>
    private static TextBlock StatValue()
    {
        var t = Ui.Text("", 15, "B.Text", wrap: false);
        t.SetResourceReference(TextBlock.FontFamilyProperty, "F.Mono");
        t.Margin = new Thickness(0, 2, 0, 0);
        return t;
    }

    private void UpdateHomeLive()
    {
        if (_liveGame is null) return;
        var target = Target;
        var session = SessionTracker.Instance;
        bool running = session.Running;
        _liveGame.Text = target.IsAuto ? (GameLocator.AutoTargetName() is { } n ? "Following " + n : "Waiting for a fullscreen game") : running ? (session.InFront ? "● In game" : "● Running") : "○ Not running";
        _liveGame.SetResourceReference(TextBlock.ForegroundProperty, running ? "B.Ok" : "B.Dim");
        _liveSession!.Text = running ? session.SessionText : "--:--:--";
        S.PlaytimeSeconds.TryGetValue(target.Id, out double total);
        _liveTotal!.Text = GameCatalog.FormatDuration(total);
    }

    private static FrameworkElement StatusRow(string label, string value, string brush, Action? go)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
        var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        row.Children.Add(dot);
        var l = Ui.Label(label.ToUpperInvariant());
        l.Width = 90;
        row.Children.Add(l);
        if (go is not null) { var b = Ui.IconButton("", "Open", go, 10); DockPanel.SetDock(b, Dock.Right); row.Children.Insert(0, b); }
        // Centre the value with the label and dot (it used to sit at the top and look misaligned),
        // and trim long values instead of overrunning on narrow 4:3 windows.
        var v = Ui.Text(value, 12, "B.Text", wrap: false);
        v.VerticalAlignment = VerticalAlignment.Center;
        v.ToolTip = value;
        row.Children.Add(v);
        return row;
    }

    private HudFrame DefconCard()
    {
        var svc = DefconService.Instance;
        if (!DefconService.Enabled)
            return Card("DEFCON", "Off for this preset.", Ui.Button("TURN ON", () => { DataStore.Preset.DefconEnabled = true; DataStore.NotifyContext(); }, "Sf.Button.Small.Primary"));
        var lvl = svc.Active;
        var color = lvl is null ? ThemeManager.Brush("B.Dim") : ThemeManager.BrushOr(lvl.Color, "B.Accent");
        var badge = new Border
        {
            Width = 64, Height = 64, BorderThickness = new Thickness(2), BorderBrush = color, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 14, 0),
            Child = new TextBlock { Text = lvl?.Short ?? "—", FontSize = 30, FontWeight = FontWeights.Bold, Foreground = color, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        ((TextBlock)badge.Child).SetResourceReference(TextBlock.FontFamilyProperty, "F.Display");
        var info = Ui.Stack(Ui.Text(lvl?.Name.ToUpperInvariant() ?? "NOT SET", 15, "B.Text"), Ui.Text(lvl?.Description ?? "Set it below or with a key.", 12, "B.Dim"),
            lvl is null ? Ui.Spacer(0) : StatusPill(svc.StatusLabel, svc.StatusBrush, svc.StatusDetail));
        if (svc.Status is DefconStatus.Assumed or DefconStatus.Stale) badge.Opacity = 0.55;
        var head = new DockPanel();
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        head.Children.Add(info);
        var levels = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var l in DataStore.Preset.Defcon.Levels)
        {
            var b = new Button { Content = l.Short, Style = Ui.Style(l.Id == lvl?.Id ? "Sf.Button.Small.Primary" : "Sf.Button.Small"), Margin = new Thickness(0, 0, 5, 5), ToolTip = l.Label + (l.Description.Length > 0 ? " — " + l.Description : ""), MinWidth = 38 };
            if (l.Id != lvl?.Id) b.Foreground = ThemeManager.BrushOr(l.Color, "B.Accent");
            string id = l.Id;
            b.Click += (_, _) => svc.SetById(id);
            levels.Children.Add(b);
        }
        var card = Card("DEFCON", null, head, levels);
        var sp = (StackPanel)card.Content;
        if (svc.Pending is { } p)
        {
            var sug = new Border { Padding = new Thickness(10, 7, 10, 8), CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 0) };
            sug.SetResourceReference(Border.BackgroundProperty, "B.Amber.Fill");
            sug.SetResourceReference(Border.BorderBrushProperty, "B.Amber");
            sug.Child = Ui.Stack(
                Ui.Text($"Suggested: {p.Level.Label}", 13, "B.Amber"),
                Ui.Label($"{p.Source} · {p.ConfidenceText} sure{(p.FromAi ? " · AI guess" : "")}"),
                p.Evidence.Length > 0 ? Ui.Hint("“" + p.Evidence + "”") : Ui.Spacer(4),
                Ui.HStack(Ui.Button("ACCEPT", svc.Accept, "Sf.Button.Small.Primary", Gesture("defcon.accept")), Ui.Button("DISMISS", svc.Dismiss)));
            sp.Children.Add(sug);
        }
        sp.Children.Add(Ui.Wrap(Ui.Button("SCAN NOW", async () => { string r = await svc.ScanNowAsync(); if (svc.Pending is null) ShowBanner(r); RenderPage(); }, tip: "Runs your enabled sources (screen, status URL) and the AI"),
            Ui.Button("DEFCON SETTINGS", () => Navigate("defcon"))));
        return card;
    }

    private FrameworkElement TimerList()
    {
        var list = new StackPanel();
        foreach (var t in TimerService.Instance.Timers)
        {
            var tt = t;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var actions = Ui.HStack(Ui.IconButton(t.PauseGlyph, t.Paused ? "Resume" : "Pause", () => { tt.TogglePause(DateTime.Now); RefreshPageSoon(); }), Ui.IconButton("", "Stop", () => TimerService.Instance.Cancel(tt)));
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var one = new ItemsControl { FontSize = 13, ItemTemplate = (DataTemplate)FindResource("Tpl.Timer") };
            one.Items.Add(t);
            row.Children.Add(one);
            list.Children.Add(row);
        }
        if (TimerService.Instance.Timers.Count == 0) list.Children.Add(Ui.Hint("No timers running."));
        return list;
    }

    private FrameworkElement TimerEntry()
    {
        const string idle = "Examples: 5m · 90s · 1:30 breach · @21:30 event · every 10m patrol (repeats)";
        var hint = Ui.Label(idle);
        hint.TextWrapping = TextWrapping.Wrap;   // was cut off with "…" on narrow cards
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(0, 0, 0, 4);
        var box = Ui.Placeholder(Ui.TextBox("", _ => { }), "e.g. 7m30s patrol");
        box.TextChanged += (_, _) =>
        {
            if (box.Text.Trim().Length == 0) { hint.Text = idle; hint.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim"); return; }
            if (TimerParser.TryParse(box.Text, out var r)) { hint.Text = $"{(r.Repeat ? "↻ " : "")}{TimerParser.Describe(r.Duration)}{(r.Label is null ? "" : " · " + r.Label.ToUpperInvariant())} → ENDS {DateTime.Now + r.Duration:HH:mm:ss}"; hint.SetResourceReference(TextBlock.ForegroundProperty, "B.Accent"); }
            else { hint.Text = "CAN'T READ THAT · TRY 5m, 1:30, @21:30"; hint.SetResourceReference(TextBlock.ForegroundProperty, "B.Alert"); }
        };
        void Go() { if (TimerParser.TryParse(box.Text, out var r)) { TimerService.Instance.Start(r); S.LastManualTimer = box.Text.Trim(); box.Text = ""; DataStore.MarkDirty(); } }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Go(); e.Handled = true; } };
        var row = new DockPanel();
        var start = Ui.Button("START", Go, "Sf.Button.Small.Primary");
        start.Margin = new Thickness(6, 2, 0, 8);
        var sw = Ui.Button("STOPWATCH", () => TimerService.Instance.StartStopwatch());
        sw.Margin = new Thickness(6, 2, 0, 8);
        DockPanel.SetDock(sw, Dock.Right); row.Children.Add(sw);
        DockPanel.SetDock(start, Dock.Right); row.Children.Add(start);
        row.Children.Add(box);
        return Ui.Stack(row, hint);
    }

    private FrameworkElement PresetChips()
    {
        var w = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var t in DataStore.Profile.TimerPresets)
        {
            var tt = t;
            var b = Ui.Button(t.Name.ToUpperInvariant(), () => TimerService.Instance.Start(tt), tip: (t.Mode == TimerMode.Stopwatch ? "Stopwatch" : TimerParser.Describe(TimeSpan.FromSeconds(t.Seconds)) + (t.Repeat ? " · repeats" : "")) + (App.Gesture("timer." + t.Id) is { Length: > 0 } g ? " · " + g : ""));
            if (t.Color.Length > 0) b.Foreground = ThemeManager.BrushOr(t.Color, "B.Accent");
            w.Children.Add(b);
        }
        return w;
    }

    private void LaunchGame()
    {
        if (Target.SteamAppId is not int id) return;
        if (id == 4000 && S.IntelEnabled) OpenUrl("steam://run/4000//-condebug/");
        else ServerQuery.LaunchSteamGame(id);
    }

    // ================================================================ NOTES

    private FrameworkElement NotesPage()
    {
        var view = new NotePadView { HostWindow = this, MinHeight = 520 };
        var sp = new StackPanel();
        sp.Children.Add(PageHeader("Note pads", $"Pads for {DataStore.Preset.Name}. Each preset keeps its own pads; switching never overwrites them.",
            Ui.Button("OPEN FLOATING PAD", () => OpenPads(null), "Sf.Button.Small.Primary", $"Resizable window over the game ({Gesture("pad.window")})"),
            Ui.Button("PAD KEYBINDS", () => { _subTab = "keys"; _keyFilter = "pad"; Navigate("overlay"); })));
        var frame = new HudFrame { Style = Ui.Style("Sf.Panel"), Content = view };
        sp.Children.Add(frame);
        return sp;
    }

    // ================================================================ OVERLAY

    private FrameworkElement OverlayPage()
    {
        if (_subTab is not ("pages" or "keys" or "crosshair" or "display")) _subTab = "pages";
        var sp = new StackPanel();
        sp.Children.Add(PageHeader("Overlay", "Build your own HUD: pages of movable elements, each with its own look. Everything here can also be done in game.",
            Ui.Button("CUSTOMIZE IN GAME", () => _overlay.SetCustomize(true), "Sf.Button.Small.Primary", Gesture("overlay.customize"))));
        sp.Children.Add(SubTabs(_subTab, t => { _subTab = t; RenderPage(); }, ("pages", "PAGES & ELEMENTS"), ("keys", "KEYBINDS"), ("crosshair", "CROSSHAIR"), ("display", "DISPLAY")));
        sp.Children.Add(_subTab switch
        {
            "keys" => KeybindsPanel(),
            "crosshair" => CrosshairPanel(),
            "display" => DisplayPanel(),
            _ => PagesPanel(),
        });
        return sp;
    }

    private FrameworkElement PagesPanel()
    {
        var pre = DataStore.Preset;
        var page = pre.Pages.FirstOrDefault(p => p.Id == _overlayPageId) ?? pre.ActivePage;
        _overlayPageId = page.Id;
        var el = page.Elements.FirstOrDefault(e => e.Id == _overlayElementId);

        // pages list
        var pages = new StackPanel();
        foreach (var pg in pre.Pages)
        {
            var p = pg;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var actions = Ui.HStack(
                Ui.IconButton(pre.DefaultPageId == p.Id ? "" : "", "Default page", () => { pre.DefaultPageId = p.Id; DataStore.MarkDirty(); RenderPage(); }),
                Ui.IconButton("", "Rename", () => { if (Ui.Prompt(this, "Rename page", p.Name) is { Length: > 0 } n) { p.Name = n.Trim(); DataStore.NotifyContext(); } }),
                Ui.ProFade(Ui.IconButton("", "Duplicate", () => { if (!Edition.CanAddPage(pre.Pages.Count)) { Ui.RequirePro(ProFeature.UnlimitedPages); return; } var c = DataStore.DeepClone(p); c.Id = Ids.New(); c.Name = DataStore.UniqueName(p.Name + " copy", pre.Pages.Select(x => x.Name)); foreach (var e in c.Elements) e.Id = Ids.New(); pre.Pages.Insert(pre.Pages.IndexOf(p) + 1, c); DataStore.NotifyContext(); }), ProFeature.UnlimitedPages, !Edition.CanAddPage(pre.Pages.Count)),
                Ui.IconButton("", "Move up", () => { Move(pre.Pages, p, -1); DataStore.NotifyContext(); }),
                Ui.IconButton("", "Move down", () => { Move(pre.Pages, p, +1); DataStore.NotifyContext(); }),
                Ui.IconButton("", "Delete", () =>
                {
                    if (pre.Pages.Count <= 1) return;
                    if (S.ConfirmDeletes && !Ui.Confirm(this, "Delete page", $"Delete \"{p.Name}\"? A backup is made first.")) return;
                    DataStore.Backup("page-deleted");
                    pre.Pages.Remove(p);
                    DataStore.NotifyContext();
                }));
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var b = new Button { Content = (pre.ActivePageId == p.Id ? "● " : "") + p.Name, Style = Ui.Style(p.Id == page.Id ? "Sf.Button.Small.Primary" : "Sf.Button.Small"), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Edit this page · ● = showing in game" };
            b.Click += (_, _) => { _overlayPageId = p.Id; _overlayElementId = ""; RenderPage(); };
            row.Children.Add(b);
            pages.Children.Add(row);
        }
        var pagesCard = Card("PAGES", "Switch pages in game with keys (Keybinds tab) or from the top of the customize menu.", pages,
            Ui.Wrap(Ui.ProFade(Ui.Button("+ NEW PAGE", () =>
            {
                if (!Edition.CanAddPage(pre.Pages.Count)) { Ui.RequirePro(ProFeature.UnlimitedPages); return; }
                if (Ui.Prompt(this, "New page", DataStore.UniqueName("Custom Layout", pre.Pages.Select(x => x.Name))) is not { Length: > 0 } n) return;
                var np = new OverlayPage { Name = n.Trim() };
                pre.Pages.Add(np);
                _overlayPageId = np.Id;
                DataStore.NotifyContext();
            }), ProFeature.UnlimitedPages, !Edition.CanAddPage(pre.Pages.Count)), Ui.Button("SHOW IN GAME", () => { EngageIfNeeded(); _overlay.SwitchPage(page.Id); RenderPage(); })));

        // elements list
        var els = new StackPanel();
        foreach (var e in Enumerable.Reverse(page.Elements))
        {
            var x = e;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            var vis = Ui.IconButton(x.Visible ? "" : "", x.Visible ? "Hide" : "Show", () => { x.Visible = !x.Visible; DataStore.MarkDirty(); _overlay.Refresh(); RenderPage(); });
            var del = Ui.IconButton("", "Remove", () => { page.Elements.Remove(x); DataStore.MarkDirty(); _overlay.Refresh(); RenderPage(); });
            DockPanel.SetDock(del, Dock.Right); row.Children.Add(del);
            DockPanel.SetDock(vis, Dock.Right); row.Children.Add(vis);
            var ic = Ui.Icon(ElementCatalog.Glyph(x.Kind), 13, x.Id == _overlayElementId ? "B.Amber" : "B.Accent");
            ic.Margin = new Thickness(0, 0, 8, 0);
            row.Children.Add(ic);
            row.Children.Add(Ui.Text(x.DisplayName + (x.Title.Length > 0 && x.Kind == ElementKind.InfoCard ? " · " + x.Title : ""), 12.5, x.Visible ? "B.Text" : "B.Muted", wrap: false));
            row.MouseLeftButtonUp += (_, _) => { _overlayElementId = x.Id; RenderPage(); };
            els.Children.Add(row);
        }
        if (page.Elements.Count == 0) els.Children.Add(Ui.Hint("No elements yet."));
        var add = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var k in ElementCatalog.Addable)
        {
            var kind = k;
            add.Children.Add(Ui.Button("+ " + ElementCatalog.Name(k).ToUpperInvariant(), () =>
            {
                if (kind == ElementKind.Video && !Ui.RequirePro(ProFeature.Video)) return;
                var ne = ElementCatalog.Create(kind, 0.5, 0.4);
                if (kind == ElementKind.NotePad) ne.PadId = Pads.Current.Id;
                page.Elements.Add(ne);
                _overlayElementId = ne.Id;
                DataStore.MarkDirty();
                _overlay.Refresh();
                RenderPage();
            }));
            if (kind == ElementKind.Video) Ui.ProFade((FrameworkElement)add.Children[^1], ProFeature.Video);
        }
        var elementsCard = Card($"ELEMENTS ON \"{page.Name.ToUpperInvariant()}\"", "Click one to edit it. Drag and resize them in game.", els, add);

        var editor = el is null
            ? Card("EDIT ELEMENT", "Pick an element on the left.")
            : Card("EDIT ELEMENT", null, ElementEditor.Build(el, () => _overlay.Refresh(),
                delete: () => { page.Elements.Remove(el); DataStore.MarkDirty(); _overlay.Refresh(); _overlayElementId = ""; RenderPage(); },
                duplicate: () => { var c = el.Clone(); page.Elements.Add(c); _overlayElementId = c.Id; DataStore.MarkDirty(); _overlay.Refresh(); RenderPage(); },
                reset: () => { ElementEditor.ResetLook(el); DataStore.MarkDirty(); _overlay.Refresh(); RenderPage(); }));
        return Columns(330, Ui.Stack(pagesCard, elementsCard), editor);
    }

    private static void Move<T>(List<T> list, T item, int dir)
    {
        int i = list.IndexOf(item), j = Math.Clamp(i + dir, 0, list.Count - 1);
        if (i == j) return;
        list.RemoveAt(i);
        list.Insert(j, item);
    }

    // ---------------------------------------------------------------- keybinds

    private string _keyFilter = "";

    private FrameworkElement KeybindsPanel()
    {
        var keys = App.Keys;
        if (keys is null) return Ui.Hint("Keybinds are still starting.");
        var prof = DataStore.Profile;
        var sp = new StackPanel();
        var rows = new StackPanel();
        var filter = Ui.Placeholder(Ui.TextBox(_keyFilter, v => { }, live: true), "Filter keybinds, e.g. pad, timer, defcon");
        filter.ToolTip = "Filter actions";
        filter.TextChanged += (_, _) => { _keyFilter = filter.Text; RenderKeyRows(rows, keys, prof); };
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var reset = Ui.Button("RESET ALL TO DEFAULTS", () =>
        {
            if (!Ui.Confirm(this, "Reset keybinds", "Put every keybind in this profile back to its default?", "RESET")) return;
            Keybinds.ResetAll(prof);
            keys.Rebuild();
            RenderPage();
        }, "Sf.Button.Small.Danger");
        reset.Margin = new Thickness(8, 2, 0, 8);
        DockPanel.SetDock(reset, Dock.Right);
        top.Children.Add(reset);
        top.Children.Add(filter);
        sp.Children.Add(top);
        string summary = keys.ProblemCount == 0 ? $"{keys.ArmedCount} keys ready, no conflicts." : $"{keys.ProblemCount} binding(s) need attention: duplicates or combos another app already owns (shown in amber).";
        sp.Children.Add(Ui.Hint(summary + " Click a combo to change it. In-game-only keys are free for other apps when the game isn't in front. Saved per profile."));
        sp.Children.Add(rows);
        RenderKeyRows(rows, keys, prof);
        return Ui.Panel(sp);
    }

    private void RenderKeyRows(StackPanel rows, KeybindHost keys, UserProfile prof)
    {
        rows.Children.Clear();
        string f = _keyFilter.Trim();
        foreach (var group in keys.Rows.Where(r => f.Length == 0 || r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Category.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Gesture.Contains(f, StringComparison.OrdinalIgnoreCase)).GroupBy(r => r.Category))
        {
            var head = Ui.H2(group.Key.ToUpperInvariant(), 11);
            head.Margin = new Thickness(0, 12, 0, 6);
            rows.Children.Add(head);
            foreach (var r in group)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = Ui.Text(r.Name, 12.5, "B.Text", wrap: false);
                name.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(name);
                var combo = new Button { Content = r.GestureText, Style = Ui.Style("Sf.Button.Small"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Click, then press the new combo. Esc cancels." };
                combo.SetResourceReference(Control.FontFamilyProperty, "F.Mono");
                var rr = r;
                combo.Click += (_, _) => CaptureKey(rr, combo, prof, keys, rows);
                Grid.SetColumn(combo, 1);
                row.Children.Add(combo);
                var inGame = new CheckBox { Content = "In-game", IsChecked = r.InGameOnly, Margin = new Thickness(0, 4, 0, 0), FontSize = 11.5, ToolTip = "Only taken while the game (or SFHud) is in front." };
                inGame.Click += (_, _) => { Keybinds.Set(prof, rr.Def.Id, rr.Gesture, inGame.IsChecked == true); keys.Rebuild(); RenderKeyRows(rows, keys, prof); };
                Grid.SetColumn(inGame, 2);
                row.Children.Add(inGame);
                var status = Ui.Label(r.Status.ToUpperInvariant(), 10);
                status.SetResourceReference(TextBlock.ForegroundProperty, r.Problem ? "B.Amber" : r.Status is "Ready" or "In-game" ? "B.Ok" : "B.Muted");
                status.ToolTip = r.Status;
                Grid.SetColumn(status, 3);
                row.Children.Add(status);
                var acts = Ui.HStack(
                    Ui.IconButton("", "Clear (unbind)", () => { Keybinds.Set(prof, rr.Def.Id, "", rr.InGameOnly); keys.Rebuild(); RenderKeyRows(rows, keys, prof); }, 11),
                    Ui.IconButton("", "Reset to default" + (rr.Def.DefaultGesture.Length > 0 ? $" ({rr.Def.DefaultGesture})" : " (unbound)"), () => { Keybinds.Reset(prof, rr.Def.Id); keys.Rebuild(); RenderKeyRows(rows, keys, prof); }, 11));
                Grid.SetColumn(acts, 4);
                row.Children.Add(acts);
                rows.Children.Add(row);
            }
        }
    }

    private void CaptureKey(BindRow row, Button button, UserProfile prof, KeybindHost keys, StackPanel rows)
    {
        keys.Suspend(true);
        button.Content = "PRESS KEYS…";
        void Done()
        {
            button.PreviewKeyDown -= OnKey;
            button.LostKeyboardFocus -= OnLost;
            keys.Suspend(false);
            RenderKeyRows(rows, keys, prof);
        }
        void OnLost(object s, KeyboardFocusChangedEventArgs e) => Done();
        void OnKey(object s, KeyEventArgs e)
        {
            e.Handled = true;
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (k == Key.Escape) { Done(); return; }
            if (SFHud.Interop.Gesture.IsModifier(k)) { button.Content = SFHud.Interop.Gesture.Format(Keyboard.Modifiers, Key.None).TrimEnd('+', 'N', 'o', 'e') + "+…"; return; }
            string g = SFHud.Interop.Gesture.Format(Keyboard.Modifiers, k);
            var clash = Keybinds.ConflictsWith(prof, row.Def.Id, g).ToList();
            string? advice = Keybinds.Advice(g);
            if (clash.Count > 0 && !Ui.Confirm(this, "Key already used", $"{g} is already used by \"{clash[0].Name}\". Use it here anyway? (The other one will show as a duplicate until you change it.)", "USE ANYWAY")) { Done(); return; }
            if (advice is not null && !Ui.Confirm(this, "Are you sure?", advice + $"\n\nBind {g} anyway?", "BIND")) { Done(); return; }
            Keybinds.Set(prof, row.Def.Id, g, row.InGameOnly || (advice is not null && Keyboard.Modifiers == ModifierKeys.Control));
            Done();
        }
        button.PreviewKeyDown += OnKey;
        button.LostKeyboardFocus += OnLost;
        button.Focus();
        Keyboard.Focus(button);
    }

    // ---------------------------------------------------------------- crosshair

    private FrameworkElement CrosshairPanel()
    {
        var shadow = new System.Windows.Shapes.Path { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var main = new System.Windows.Shapes.Path { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        var box = new Grid { Children = { shadow, main }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var preview = new Border { Height = 160, CornerRadius = new CornerRadius(6), Child = new Viewbox { Child = box, Width = 120, Height = 120 }, Margin = new Thickness(0, 0, 0, 10) };
        preview.Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x4A, 0x55), Color.FromRgb(0x8A, 0x7A, 0x60), 45);
        void Draw() { CrosshairGeometry.Apply(main, shadow, box, S); box.Opacity = S.CrosshairEnabled ? 1 : 0.35; _overlay.ApplyCrosshair(); DataStore.MarkDirty(); }
        Draw();

        var styles = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var st in Enum.GetValues<CrosshairStyle>())
        {
            var chip = new ToggleButton { Content = st.ToString().ToUpperInvariant(), Style = Ui.Style("Sf.Chip"), IsChecked = S.CrosshairStyle == st };
            var s = st;
            chip.Click += (_, _) => { S.CrosshairStyle = s; foreach (ToggleButton c in styles.Children) c.IsChecked = c == chip; Draw(); };
            styles.Children.Add(chip);
        }
        var left = Card("CROSSHAIR", "Drawn by SFHud on its overlay; game files and memory are never touched. Off by default.",
            preview,
            Ui.Switch("Crosshair on", S.CrosshairEnabled, v => { S.CrosshairEnabled = v; Draw(); }),
            Ui.Label("STYLE"), styles,
            Ui.Label("COLOUR"), Ui.ColorPicker(S.CrosshairColor, v => { if (v.Length > 0) { S.CrosshairColor = v; Draw(); } }, allowDefault: false));
        var right = Card("SHAPE", null,
            Ui.Slider("Size", 6, 64, S.CrosshairSize, v => $"{v:0} px", v => { S.CrosshairSize = Math.Round(v); Draw(); }, step: 1),
            Ui.Slider("Thickness", 1, 6, S.CrosshairThickness, v => $"{v:0.#} px", v => { S.CrosshairThickness = Math.Round(v * 2) / 2; Draw(); }, step: 0.5),
            Ui.Slider("Gap (left = auto)", -1, 20, S.CrosshairGap, v => v < 0 ? "AUTO" : $"{v:0} px", v => { S.CrosshairGap = Math.Round(v); Draw(); }, step: 1),
            Ui.Slider("Opacity", 0.1, 1, S.CrosshairOpacity, v => $"{v:P0}", v => { S.CrosshairOpacity = Math.Round(v, 2); Draw(); }),
            Ui.Switch("Center dot", S.CrosshairCenterDot, v => { S.CrosshairCenterDot = v; Draw(); }),
            Ui.Switch("Outline", S.CrosshairOutline, v => { S.CrosshairOutline = v; Draw(); }),
            Ui.Slider("Outline thickness", 0.5, 4, S.CrosshairOutlineThickness, v => $"{v:0.#} px", v => { S.CrosshairOutlineThickness = Math.Round(v * 2) / 2; Draw(); }, step: 0.5),
            Ui.Label("OUTLINE COLOUR"), Ui.ColorPicker(S.CrosshairOutlineColor, v => { if (v.Length > 0) { S.CrosshairOutlineColor = v; Draw(); } }, allowDefault: false));

        var presets = new StackPanel();
        foreach (var pr in DataStore.Profile.CrosshairPresets)
        {
            var p = pr;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var del = Ui.IconButton("", "Delete", () => { DataStore.Profile.CrosshairPresets.Remove(p); DataStore.MarkDirty(); RenderPage(); });
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            row.Children.Add(Ui.Button($"{p.Name.ToUpperInvariant()}  ·  {p.Style}", () => { CrosshairPresets.Apply(p, S); S.CrosshairEnabled = true; Draw(); RenderPage(); }));
            presets.Children.Add(row);
        }
        var presetCard = Card("PRESETS", "Save a few and switch between them (also in the in-game menu).", presets,
            Ui.Button("SAVE CURRENT AS PRESET…", () => { if (Ui.Prompt(this, "Crosshair preset", $"{S.CrosshairStyle} {S.CrosshairColor}") is { Length: > 0 } n) { DataStore.Profile.CrosshairPresets.Add(CrosshairPresets.From(S, n.Trim())); DataStore.MarkDirty(); RenderPage(); } }, "Sf.Button.Small.Primary"),
            Ui.Button("EDIT LIVE IN GAME", () => Run("overlay.crosshair"), tip: Gesture("overlay.crosshair")));
        return Columns(320, left, right, presetCard);
    }

    // ---------------------------------------------------------------- display

    private FrameworkElement DisplayPanel()
    {
        void C() { DataStore.MarkDirty(); _overlay.Refresh(); }
        var a = Card("OVERLAY", null,
            Ui.Slider("Overlay opacity", 0.2, 1, S.OverlayOpacity, v => $"{v:P0}", v => { S.OverlayOpacity = Math.Round(v, 2); C(); }),
            Ui.Slider("Overlay scale", 0.6, 3, S.UiScale, v => $"{v:0.00}×", v => { S.UiScale = Math.Round(v, 2); C(); }, "Size of every element. Per-element text size is in the element editor."),
            Ui.Switch("Scale with game resolution", S.ScaleWithResolution, v => { S.ScaleWithResolution = v; C(); }, "1080p = 1×, so a layout looks the same at 1440p or 4K. On 4:3 and 5:4 screens it also fits the narrower width."),
            Ui.Slider("Smallest text on low resolutions", 0.5, 1.2, S.MinReadableScale, v => $"{v:0.00}×", v => { S.MinReadableScale = Math.Round(v, 2); C(); }, "Floor for resolution scaling, so notes stay readable at CRT / 4:3 resolutions like 800×600 and 1024×768. 1080p is 1×."),
            Ui.Switch("Scale the in-game menu and toasts with the screen", S.ScaleOverlayMenu, v => { S.ScaleOverlayMenu = v; C(); }, "Bigger on 1440p / 4K, fitted on 4:3 screens."),
            Ui.Switch("Keep clear of the game's chat", S.AvoidGameHud, v => { S.AvoidGameHud = v; C(); }, "When you drop an element on the game's chat box (amber box in customize mode), it's moved just clear and saved there, so you can always read chat. Hold Alt while dropping to place it anyway. Elements never move by themselves in game."),
            Ui.Switch("Show overlay when SFHud starts", S.AutoEngage, v => { S.AutoEngage = v; DataStore.MarkDirty(); }),
            Ui.Switch("Distraction-free (only crosshair and toasts)", S.DistractionFree, v => { S.DistractionFree = v; C(); }, Gesture("overlay.distraction")),
            Ui.Switch("Hide SFHud from screenshots / OBS", S.ExcludeFromCapture, v => { S.ExcludeFromCapture = v; _overlay.ApplyCaptureAffinity(); DataStore.MarkDirty(); }, "Uses Windows' capture exclusion (Windows 10 2004+)."),
            Ui.Switch("Pop-ups only over the game (like Discord's overlay)", S.PopupsFollowGame, v => { S.PopupsFollowGame = v; DataStore.MarkDirty(); }, "Pad, quick-note, AI and timer windows open from their keys only over your hooked game (or SFHud), hide when you leave the game, and come back when you return."));
        var b = Card("ELEMENT GROUPS", "Quick on/off for kinds of elements across all pages.",
            Ui.Switch("Info cards", S.PinsVisible, v => { S.PinsVisible = v; C(); }, Gesture("overlay.cards")),
            Ui.Switch("Timers", S.TimersVisible, v => { S.TimersVisible = v; C(); }, Gesture("timers.toggle")),
            Ui.Switch("Music player", S.MediaVisible, v => _overlay.SetMusicVisible(v), Gesture("media.toggleui")),
            Ui.Switch("Server intel", S.IntelEnabled, v => { S.IntelEnabled = v; if (v) IntelService.Instance.Start(); else IntelService.Instance.Stop(); C(); }));
        var c = Card("LAYOUT EDITING", null,
            Ui.Switch("Snap to grid", S.SnapToGrid, v => { S.SnapToGrid = v; DataStore.MarkDirty(); }, "Hold Alt while dragging to move freely."),
            Ui.Slider("Grid size", 4, 32, S.GridSize, v => $"{v:0}", v => { S.GridSize = Math.Round(v); DataStore.MarkDirty(); }, step: 2),
            Ui.Switch("Alignment guides", S.ShowGuides, v => { S.ShowGuides = v; DataStore.MarkDirty(); }),
            Ui.Switch("Lock layout", S.LayoutLocked, v => { S.LayoutLocked = v; DataStore.MarkDirty(); }, "Nothing can be moved or resized until unlocked."),
            Ui.Switch("Toasts", S.Toasts, v => { S.Toasts = v; DataStore.MarkDirty(); }),
            Ui.Slider("Toast duration", 2, 15, S.ToastSeconds, v => $"{v:0.#} s", v => { S.ToastSeconds = Math.Round(v * 2) / 2; DataStore.MarkDirty(); }, step: 0.5),
            Ui.Button("RESET POSITIONS ON CURRENT PAGE", () => _overlay.ResetPositions(), "Sf.Button.Small.Danger"));
        return Columns(320, a, TopBarCard(), b, c);
    }

    /// <summary>Settings › Top bar: what the status strip shows, its order, and the clock format / time zone.</summary>
    private HudFrame TopBarCard()
    {
        void C() { DataStore.MarkDirty(); _overlay.Refresh(); }
        var items = new StackPanel();
        // Selected items first (in their order, with ↑ ↓), then the ones that are off.
        var order = S.TopBarItems.Where(id => TopBar.All.Any(a => a.Id == id)).Concat(TopBar.All.Select(a => a.Id).Where(id => !S.TopBarItems.Contains(id))).ToList();
        foreach (var id in order)
        {
            string x = id;
            bool on = S.TopBarItems.Contains(id);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            if (on)
            {
                var arrows = Ui.HStack(Ui.IconButton("", "Move left in the bar", () => { TopBar.Move(x, -1); C(); RenderPage(); }, 10),
                                       Ui.IconButton("", "Move right in the bar", () => { TopBar.Move(x, 1); C(); RenderPage(); }, 10));
                DockPanel.SetDock(arrows, Dock.Right);
                row.Children.Add(arrows);
            }
            var sw = Ui.Switch(TopBar.NameOf(id), on, v => { TopBar.Toggle(x, v); C(); RenderPage(); });
            sw.Margin = new Thickness(0, 2, 0, 4);
            row.Children.Add(sw);
            items.Children.Add(row);
        }
        var preview = Ui.Label(string.Join("  ·  ", TopBar.Compose(Target.Name, SessionTracker.Instance.SessionText, SessionTracker.Instance.Running).Select(p => p.Plain)));
        preview.TextWrapping = TextWrapping.Wrap;
        var zones = TopBar.TimeZoneIds();
        return Card("TOP BAR & CLOCK", "Pick what the overlay's top bar (status strip) shows and in which order. The clock settings also apply to the Clock element.",
            items,
            Ui.Spacer(4), Ui.Label("PREVIEW"), preview, Ui.Spacer(8),
            Ui.Switch("24-hour clock", S.Clock24h, v => { S.Clock24h = v; C(); RenderPage(); }, "Off = 12-hour with AM / PM."),
            Ui.Switch("Show seconds", S.ClockSeconds, v => { S.ClockSeconds = v; C(); RenderPage(); }),
            Ui.Row("Time zone", Ui.Combo(zones, S.ClockTimeZone, v => { S.ClockTimeZone = v; C(); RenderPage(); }, TopBar.TimeZoneName),
                "Show another zone's time instead of this PC's, e.g. your server's or your unit's time."),
            Ui.Row("Label", Ui.TextBox(S.ClockLabel, v => { S.ClockLabel = v.Trim(); C(); }), "Optional tag after the time, e.g. SERVER or EST."),
            Ui.Hint($"Now: {TopBar.ClockText()}"));
    }

    // ================================================================ DEFCON

    /// <summary>One row per level: which overlay page it shows, and one click to make a layout for it.</summary>
    private FrameworkElement LevelLayoutsCard()
    {
        if (!Edition.Has(ProFeature.LevelLayouts))
            return Card("LAYOUT PER LEVEL", null, Ui.ProPanel(ProFeature.LevelLayouts, "Give each DEFCON level its own overlay page (e.g. a combat layout at DEFCON 1) and the overlay switches by itself when the level changes. In SFHud Free you can still switch pages with keys and show elements only at chosen levels (\"Show at DEFCON\" in any element's editor)."));
        var pre = DataStore.Preset;
        var cfg = pre.Defcon;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 150 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star), MinWidth = 170 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pageItems = new[] { "" }.Concat(pre.Pages.Select(p => p.Id)).ToList();
        int r = 0;
        foreach (var lv in cfg.Levels)
        {
            var l = lv;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 10, 3) };
            name.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = ThemeManager.BrushOr(l.Color, "B.Accent"), Margin = new Thickness(0, 0, 8, 0) });
            name.Children.Add(Ui.Text(l.Label + (l.Id == cfg.ActiveLevelId ? "  ●" : ""), 12.5, l.Id == cfg.ActiveLevelId ? "B.Amber" : "B.Text", wrap: false));
            Grid.SetRow(name, r);
            grid.Children.Add(name);
            var combo = Ui.Combo(pageItems, l.AutoPageId, v => { DefconLayouts.Assign(l, v); RefreshPageSoon(); }, id => id.Length == 0 ? "Stay on current page" : pre.Pages.FirstOrDefault(p => p.Id == id)?.Name ?? "?");
            combo.Margin = new Thickness(0, 3, 8, 3);
            Grid.SetRow(combo, r); Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);
            var acts = Ui.HStack(
                Ui.Button("+ NEW", () =>
                {
                    var src = pre.Pages.FirstOrDefault(p => p.Id == _overlayPageId) ?? pre.ActivePage;
                    if (DefconLayouts.CreateFor(l, src) is { } np) { _overlayPageId = np.Id; ShowBanner($"Made \"{np.Name}\" from \"{src.Name}\". It shows whenever {l.Label} is active; press EDIT to arrange it."); }
                    else ShowBanner("Up to 30 pages per preset.");
                }, tip: "Copy the page you're editing into a new page for this level"),
                Ui.Button("EDIT", () =>
                {
                    if (DefconLayouts.PageFor(l) is not { } pg) { ShowBanner($"{l.Label} has no layout yet. Press + NEW first, or pick a page."); return; }
                    EngageIfNeeded();
                    _overlay.SwitchPage(pg.Id);
                    _overlay.SetCustomize(true);
                }, tip: "Open this level's page in the in-game editor"));
            acts.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(acts, r); Grid.SetColumn(acts, 2);
            grid.Children.Add(acts);
            r++;
        }
        return Card("LAYOUT PER LEVEL", "Pick which overlay page each level shows. It switches automatically when the level changes. + NEW copies the page you're editing so you can tweak it for that level.",
            grid,
            Ui.Spacer(4),
            Ui.Switch("Back to the default page at levels without a layout", cfg.ReturnToDefaultPage, v => { cfg.ReturnToDefaultPage = v; DataStore.MarkDirty(); },
                "On: leaving e.g. DEFCON 1 goes back to your normal page. Off: the overlay stays on the last level's page."));
    }

    private FrameworkElement DefconPage()
    {
        var pre = DataStore.Preset;
        var cfg = pre.Defcon;
        var svc = DefconService.Instance;
        var sp = new StackPanel();
        sp.Children.Add(PageHeader("DEFCON", $"Levels for {pre.Name}. Each server preset has its own table, layouts and pads per level.",
            Ui.Switch("DEFCON on for this preset", pre.DefconEnabled, v => { pre.DefconEnabled = v; DataStore.NotifyContext(); })));
        if (!pre.DefconEnabled) { sp.Children.Add(Ui.Hint("Turn it on to use levels, detection and per-level layouts.")); return sp; }
        sp.Children.Add(LevelLayoutsCard());

        // Levels editor
        var levels = new StackPanel();
        for (int i = 0; i < cfg.Levels.Count; i++)
        {
            var l = cfg.Levels[i];
            var head = new DockPanel();
            var sw = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), Background = ThemeManager.BrushOr(l.Color, "B.Accent"), Margin = new Thickness(0, 0, 10, 0) };
            head.Children.Add(sw);
            head.Children.Add(Ui.H2($"{l.Label}{(l.Id == cfg.ActiveLevelId ? "  · ACTIVE" : "")}", 12));
            var body = new StackPanel();
            body.Children.Add(Ui.Row("Number", Ui.TextBox(l.Number?.ToString() ?? "", v => { l.Number = int.TryParse(v.Trim(), out int n) ? n : null; DataStore.MarkDirty(); }), "What servers announce (\"DEFCON 3\"). Leave empty for named-only levels like LOCKDOWN."));
            body.Children.Add(Ui.Row("Name", Ui.TextBox(l.Name, v => { l.Name = v.Trim(); DataStore.MarkDirty(); })));
            body.Children.Add(Ui.Row("Description", Ui.TextBox(l.Description, v => { l.Description = v.Trim(); DataStore.MarkDirty(); }, multiline: true, minHeight: 44), "Shown on the overlay badge and in the change toast."));
            body.Children.Add(Ui.Row("Also means", Ui.TextBox(l.Aliases, v => { l.Aliases = v; DataStore.MarkDirty(); }), "Comma-separated words that mean this level on your server (e.g. lockdown, code red). Used by detection."));
            body.Children.Add(Ui.Label("COLOUR"));
            body.Children.Add(Ui.ColorPicker(l.Color, v => { if (v.Length > 0) { l.Color = v; DataStore.MarkDirty(); } }, allowDefault: false));
            var pageItems = new[] { "" }.Concat(pre.Pages.Select(p => p.Id)).ToList();
            body.Children.Add(Ui.Row("Switch to page", Ui.Combo(pageItems, l.AutoPageId, v => { l.AutoPageId = v; DataStore.MarkDirty(); }, id => id.Length == 0 ? "Stay on current page" : pre.Pages.First(p => p.Id == id).Name), "Show this overlay page automatically when the level becomes active."));
            var padItems = new[] { "" }.Concat(pre.Pads.Select(p => p.Id)).ToList();
            body.Children.Add(Ui.ProLock(Ui.Row("Level pad", Ui.Combo(padItems, l.PadId, v => { l.PadId = v; if (v.Length > 0 && pre.Pad(v) is { } pd) pd.DefconLevelId = l.Id; DataStore.NotifyNotes(); }, id => id.Length == 0 ? "None" : pre.Pads.First(p => p.Id == id).Title), "A pad that only appears while this level is active (its notes are kept when the level changes)."), ProFeature.LevelLayouts));
            var lvl = l;
            body.Children.Add(Ui.Button("+ CREATE A PAD FOR THIS LEVEL", () =>
            {
                if (!Ui.RequirePro(ProFeature.LevelLayouts)) return;
                var pad = Pads.Create($"{lvl.Label} notes", "DEFCON");
                pad.DefconLevelId = lvl.Id;
                pad.Color = lvl.Color;
                lvl.PadId = pad.Id;
                DataStore.NotifyContext();
            }));
            body.Children.Add(Ui.Switch("Toast when it becomes active", l.Toast, v => { l.Toast = v; DataStore.MarkDirty(); }));
            body.Children.Add(Ui.Switch("Flash the DEFCON badge", l.Flash, v => { l.Flash = v; DataStore.MarkDirty(); }));
            body.Children.Add(Ui.Row("Sound", SoundCombo(l.Sound, v => { l.Sound = v; DataStore.MarkDirty(); }, allowDefault: true)));
            body.Children.Add(Ui.Slider("Overlay opacity at this level", 0, 1, l.OverlayOpacity, v => v <= 0.01 ? "NORMAL" : $"{v:P0}", v => { l.OverlayOpacity = v <= 0.01 ? 0 : Math.Round(v, 2); DataStore.MarkDirty(); }));
            body.Children.Add(Ui.Hint("DEFCON-specific elements: pick levels under \"Show at DEFCON\" in any element's editor. Keys for each level are in Overlay › Keybinds › DEFCON levels."));
            body.Children.Add(Ui.Wrap(
                Ui.Button("SET ACTIVE", () => svc.SetById(lvl.Id), "Sf.Button.Small.Primary"),
                Ui.Button("MOVE UP", () => { Move(cfg.Levels, lvl, -1); DataStore.NotifyContext(); }),
                Ui.Button("MOVE DOWN", () => { Move(cfg.Levels, lvl, +1); DataStore.NotifyContext(); }),
                Ui.Button("REMOVE LEVEL", () =>
                {
                    if (cfg.Levels.Count <= 1) return;
                    if (S.ConfirmDeletes && !Ui.Confirm(this, "Remove level", $"Remove {lvl.Label}? Pads and notes tied to it are kept and become visible at every level.")) return;
                    cfg.Levels.Remove(lvl);
                    foreach (var pd in pre.Pads.Where(p => p.DefconLevelId == lvl.Id)) pd.DefconLevelId = "";
                    foreach (var pg in pre.Pages) foreach (var e in pg.Elements) e.DefconFilter.Remove(lvl.Id);
                    if (cfg.ActiveLevelId == lvl.Id) cfg.ActiveLevelId = "";
                    DataStore.NotifyContext();
                }, "Sf.Button.Small.Danger")));
            levels.Children.Add(new Expander { Header = head, Content = body, IsExpanded = false });
        }
        var levelsCard = Card("LEVELS", "Top = most alert. Rename, recolour, reorder, add or remove. Six by default; any number works.", levels,
            Ui.Wrap(
                Ui.Button("+ ADD LEVEL", () =>
                {
                    int next = (cfg.Levels.Max(x => x.Number) ?? 0) + 1;
                    cfg.Levels.Add(new DefconLevel { Number = next, Name = $"Level {next}", Color = "#9AA4B2" });
                    DataStore.NotifyContext();
                }, "Sf.Button.Small.Primary"),
                Ui.Button("+ ADD NAMED LEVEL", () =>
                {
                    if (Ui.Prompt(this, "Named level", "LOCKDOWN", "For servers that use words instead of numbers.") is not { Length: > 0 } n) return;
                    cfg.Levels.Insert(0, new DefconLevel { Name = n.Trim(), Aliases = n.Trim().ToLowerInvariant(), Color = "#FF4D5E" });
                    DataStore.NotifyContext();
                }),
                Ui.Button("RESET TO 6 LEVELS", () =>
                {
                    if (!Ui.Confirm(this, "Reset levels", "Replace this preset's levels with the default DEFCON 1-6? Pads and elements tied to removed levels become visible at every level.", "RESET")) return;
                    DataStore.Backup("defcon-reset");
                    var det = cfg.Detection;
                    pre.Defcon = DefconConfig.Default(6);
                    pre.Defcon.Detection = det;
                    foreach (var pd in pre.Pads) pd.DefconLevelId = "";
                    foreach (var pg in pre.Pages) foreach (var e in pg.Elements) e.DefconFilter.Clear();
                    DataStore.NotifyContext();
                }, "Sf.Button.Small.Danger")));

        // Detection
        var det = cfg.Detection;
        var paste = Ui.TextBox("", _ => { }, multiline: true, minHeight: 60);
        paste.ToolTip = "Paste a server announcement, status page text or MOTD";
        var pasteResult = Ui.Hint("");
        var numbers = cfg.Levels.Where(l => l.Number is not null).Select(l => l.Number!.Value).DefaultIfEmpty(6).ToList();
        var detectCard = Card("DETECTION", $"SFHud starts on DEFCON {det.FallbackNumber} marked ASSUMED. The screen region is the main detector: a level is applied as AUTO-DETECTED only after it has been read the same way several times in a row. If readings stop, it shows as STALE, then goes back to the assumed fallback. With AI auto-switch on, the local AI also switches the level from announcements it reads (AI-DETECTED). Press your “Back to default DEFCON” key (Ctrl+Alt+End) to undo any automatic switch. SFHud never reads game memory or injects into the game.",
            Ui.Switch("Detection on", det.Enabled, v => { det.Enabled = v; DataStore.MarkDirty(); }),
            Ui.ProLock(Ui.Switch("Read a screen region (Windows OCR)", det.Ocr, v => { det.Ocr = v; DataStore.MarkDirty(); }, "Best source when your server shows the level on its HUD (e.g. Superior Servers CWRP). Only the region you pick is captured. Works in borderless / windowed mode."), ProFeature.DefconAutoDetect),
            Ui.ProLock(Ui.Wrap(Ui.Button("PICK REGION IN GAME", () => { EngageIfNeeded(); _overlay.PickRegion(r => { det.OcrX = r.X; det.OcrY = r.Y; det.OcrW = r.Width; det.OcrH = r.Height; det.Ocr = true; DataStore.MarkDirty(); RefreshPageSoon(); }); }),
                    Ui.Button("TEST READ", async () => { pasteResult.Text = await svc.RunOcrAsync(); })), ProFeature.DefconAutoDetect),
            Spaced(Ui.Label($"REGION  x {det.OcrX:P0} · y {det.OcrY:P0} · {det.OcrW:P0} × {det.OcrH:P0}"), 0, 10),
            Ui.Slider("Read every", 2, 30, det.OcrSeconds, v => $"{v:0} s", v => { det.OcrSeconds = (int)v; DataStore.MarkDirty(); }, "Only while the game is in front. A tiny region read costs a few ms.", 1),
            Ui.Spacer(4),
            Spaced(Ui.Label("FALLBACK & VERIFICATION"), 6, 8),
            Ui.Switch("Start on the assumed fallback", det.AssumeOnStartup, v => { det.AssumeOnStartup = v; DataStore.MarkDirty(); }, "On launch and preset switch, show the fallback labelled ASSUMED until a reading is verified."),
            Ui.Slider("Fallback level", numbers.Min(), numbers.Max(), det.FallbackNumber, v => $"DEFCON {v:0}", v => { det.FallbackNumber = (int)v; DataStore.MarkDirty(); }, "The safe default shown when nothing is verified.", 1),
            Ui.Switch("Apply verified screen readings automatically", det.AutoApplyVerifiedScreen, v => { det.AutoApplyVerifiedScreen = v; DataStore.MarkDirty(); }, "Off = a verified reading becomes a suggestion you confirm."),
            Ui.Slider("Matching reads needed", 2, 6, det.VerifyReads, v => $"{v:0} in a row", v => { det.VerifyReads = (int)v; DataStore.MarkDirty(); }, $"At {det.OcrSeconds} s per read, {det.VerifyReads} reads ≈ {det.OcrSeconds * det.VerifyReads} s to confirm a change.", 1),
            Ui.Slider("Minimum confidence per read", 0.7, 0.97, det.VerifyConfidence, v => $"{v:P0}", v => { det.VerifyConfidence = Math.Round(v, 2); DataStore.MarkDirty(); }),
            Ui.Slider("Stale after", 10, 300, det.StaleAfterSeconds, v => $"{v:0} s", v => { det.StaleAfterSeconds = (int)v; DataStore.MarkDirty(); }, "No confirming reading for this long → the level is shown as STALE.", 5),
            Ui.Slider("Back to assumed after", 30, 1800, det.AssumeAfterSeconds, v => v >= 60 ? $"{v / 60:0.#} min" : $"{v:0} s", v => { det.AssumeAfterSeconds = (int)v; DataStore.MarkDirty(); }, $"Then the overlay returns to the DEFCON {det.FallbackNumber} fallback, marked ASSUMED.", 10),
            Ui.Spacer(4),
            Ui.ProLock(Ui.Row("Status URL", Ui.TextBox(det.StatusUrl, v => { det.StatusUrl = v.Trim(); DataStore.MarkDirty(); }), "Optional: a page or JSON endpoint your server provides that states the level. Only contacted if you set it."), ProFeature.DefconAutoDetect),
            Ui.Slider("Check URL every", 15, 600, det.StatusUrlSeconds, v => $"{v:0} s", v => { det.StatusUrlSeconds = (int)v; DataStore.MarkDirty(); }, step: 5),
            Ui.Switch("Watch console.log announcements", det.ConsoleLog, v => { det.ConsoleLog = v; DataStore.MarkDirty(); }, "Garry's Mod writes console.log with -condebug. Chat-based; least reliable, so it only suggests."),
            Ui.ProLock(Ui.Switch("Use the local AI", det.AiAssist, v => { det.AiAssist = v; DataStore.MarkDirty(); }, "Runs on your PC (Local AI page). Nothing is sent anywhere."), ProFeature.LocalAi),
            Ui.ProLock(Ui.Switch("AI switches DEFCON automatically", det.AiAutoApply, v => { det.AiAutoApply = v; DataStore.MarkDirty(); }, "While the game is open, the AI reads new console / screen / status text and switches the level when the words it cites are really there. Exact \"DEFCON n\" numbers always beat the AI's guess. Ctrl+Alt+End (Back to default DEFCON) undoes it and blocks that level for 10 minutes. Off = the AI only suggests."), ProFeature.LocalAi),
            Ui.Slider("AI looks every", 10, 120, det.AiAutoSeconds, v => $"{v:0} s", v => { det.AiAutoSeconds = (int)v; DataStore.MarkDirty(); }, "Only when there is new text, so it stays idle most of the time.", 5),
            Ui.Slider("AI minimum confidence", 0.4, 0.75, det.AiMinConfidence, v => $"{v:P0}", v => { det.AiMinConfidence = Math.Round(v, 2); DataStore.MarkDirty(); }),
            Ui.Switch("Auto-apply confident rule matches", det.AutoApply == AutoApplyMode.HighConfidence, v => { det.AutoApply = v ? AutoApplyMode.HighConfidence : AutoApplyMode.Never; DataStore.MarkDirty(); }, "Exact \"DEFCON n\" matches from console.log or the status URL. Always on while AI auto-switch is on."),
            Ui.Slider("Auto-apply threshold", 0.8, 0.99, det.Threshold, v => $"{v:P0}", v => { det.Threshold = Math.Round(v, 2); DataStore.MarkDirty(); }),
            Ui.Spacer(6),
            Ui.Label("PASTED SERVER STATUS / ANNOUNCEMENT"),
            paste,
            Ui.Wrap(Ui.Button("ANALYZE TEXT", async () => { pasteResult.Text = await svc.AnalyzeTextAsync(paste.Text); }, "Sf.Button.Small.Primary"),
                    Ui.Button("SCAN ALL SOURCES NOW", async () => { pasteResult.Text = await svc.ScanNowAsync(); })),
            pasteResult,
            svc.LastOcrText.Length > 0 ? Ui.Hint("Last screen read: " + svc.LastOcrText) : Ui.Spacer(0),
            svc.LastUrlText.Length > 0 ? Ui.Hint("Last status URL text: " + svc.LastUrlText) : Ui.Spacer(0));

        var history = new StackPanel();
        foreach (var h in svc.History.Take(12)) history.Children.Add(Ui.Label(h));
        if (svc.History.Count == 0) history.Children.Add(Ui.Hint("No changes yet this session."));
        var current = Ui.Stack(DefconCard(), Card("HISTORY", null, history));
        sp.Children.Add(Columns(360, Ui.Stack(current, levelsCard), detectCard));
        return sp;
    }

    private static ComboBox SoundCombo(string current, Action<string> set, bool allowDefault)
    {
        var items = new List<string>();
        if (allowDefault) items.Add("");
        items.AddRange(SoundService.All().Select(s => s.Id));
        if (!allowDefault) items.Insert(0, "none");
        var c = Ui.Combo(items, current.Length == 0 && !allowDefault ? "none" : current, v => { string val = v == "none" ? "" : v; set(val); SoundService.Play(val, force: true); },
            id => id.Length == 0 ? "Default" : id == "none" ? "No sound" : SoundService.NameOf(id));
        c.ToolTip = "Plays a preview when you pick one";
        return c;
    }
}
