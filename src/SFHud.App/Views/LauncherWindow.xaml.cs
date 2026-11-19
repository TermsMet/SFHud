using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SFHud.Controls;
using SFHud.Interop;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>
/// SFHud's main window: a slim shell (title bar with search, save indicator and the SFHud Pro button; icon sidebar;
/// footer) whose pages are built in code from the shared <see cref="Ui"/> helpers. Also the hub that
/// routes every keybind action and service event.
/// </summary>
public partial class LauncherWindow : Window
{
    private static readonly (string Id, string Label, string Tip)[] NavItems =
    {
        ("home", "Home", "Overview: game, DEFCON, timers, music, status"),
        ("notes", "Note pads", "All your pads, tabs and notes"),
        ("overlay", "Overlay", "Pages, elements, keybinds, crosshair and display"),
        ("defcon", "DEFCON", "Levels, per-level layouts and detection"),
        ("timers", "Timers", "Timers, stopwatches, presets and sounds"),
        ("ai", "Local AI", "Offline assistant and model settings"),
        ("presets", "Presets", "Server presets and personal profiles"),
        ("games", "Games & servers", "Which apps get the overlay, favourite servers"),
        ("settings", "Settings", "Appearance, sounds, music, behaviour, data and backups"),
        ("pro", "SFHud Pro", "One-time upgrade: local AI, DEFCON auto-detection, videos, unlimited pages and profiles"),
    };

    private readonly Dictionary<string, RadioButton> _nav = new();
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly OverlayWindow _overlay = new();
    private readonly NativeMethods.WinEventDelegate _fgHookProc;
    private IntPtr _fgHook;
    private HotkeyManager? _hotkeys;
    private CompanionWindow? _companion;
    private QuickNoteWindow? _quickNote;
    private TimerPromptWindow? _timerPrompt;
    private NotePadWindow? _padWindow;
    private AiWindow? _aiWindow;
    /// <summary>SFHud pop-ups hidden because another app came in front of the game; shown again when it's back.</summary>
    private readonly List<Window> _parked = new();
    private string _page = "home";
    private bool _gameInFront;
    private bool _closingForReal;

    private static AppSettings S => DataStore.Settings;
    private static GameTarget Target => GameCatalog.Get(S);

    public LauncherWindow()
    {
        InitializeComponent();
        _fgHookProc = OnForegroundChanged;
        RestoreGeometry();
        BuildNav();

        DataStore.SaveStateChanged += UpdateSaveIndicator;
        DataStore.ContextChanged += OnContextChanged;
        ThemeManager.Changed += ApplyAppearance;

        TimerService.Instance.Finished += t =>
        {
            _overlay.ShowToast(t.Repeat ? $"{t.Label} · ROUND {t.Round - 1} DONE" : "TIMER DONE", t.Repeat ? "Next round started." : t.Label, urgent: true, t.Color);
            if (_page is "home" or "timers") RefreshPageSoon();
        };
        TimerService.Instance.Timers.CollectionChanged += (_, _) => { if (_page is "home" or "timers") RefreshPageSoon(); };
        DefconService.Instance.LevelChanged += OnDefconChanged;
        DefconService.Instance.SuggestionChanged += OnDefconSuggestion;
        DefconService.Instance.StatusChanged += () => { if (_page is "home" or "defcon") RefreshPageSoon(); };
        IntelService.Instance.AlertRaised += ev => { _overlay.ShowToast("ALERT", ev.Text, urgent: true); SoundService.Play(S.NotifySound); };
        ChatWatch.Alert += (title, text, urgent, sound) => Dispatcher.BeginInvoke(() => { _overlay.ShowToast(title, text, urgent); SoundService.Play(sound.Length > 0 ? sound : urgent ? S.AlertSound : S.NotifySound); });
        ChatWatch.CountdownRequested += (label, minutes) => Dispatcher.BeginInvoke(() =>
        {
            // One countdown per announcement: a repeat of "event in 10 min" two minutes later just updates it.
            foreach (var old in TimerService.Instance.Timers.Where(t => t.Label == label).ToList()) TimerService.Instance.Cancel(old);
            TimerService.Instance.Start(TimeSpan.FromMinutes(minutes), label, color: "#FFC400");
        });
        LocalAi.Instance.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(() => { if (_page == "ai") RefreshPageSoon(); UpdateFooter(); });
        MediaService.Instance.Changed += () => { if (_page == "home") RefreshPageSoon(); };

        _overlay.OverlayStateChanged += _ => UpdateFooter();
        _overlay.CustomizeChanged += on => { UpdateFooter(); if (!on && _page == "overlay") RefreshPageSoon(); if (!on) App.Keys?.Rebuild(); };
        _overlay.CommandRequested += Run;
        _overlay.OtherAppInFrontChanged += other => { if (other) ParkPopups(); };
        _overlay.InGameChanged += inGame => { if (inGame) UnparkPopups(); else if (!OursInFront()) ParkPopups(); };
        _overlay.InGameChanged += inGame =>
        {
            // Music playing but the overlay player switched off: say once per session how to bring it back.
            if (!inGame || S.MediaVisible || _musicHintShown) return;
            var m = MediaService.Instance;
            if (!m.HasSession || !m.Playing) return;
            _musicHintShown = true;
            Dispatcher.BeginInvoke(() => _overlay.ShowToast("MUSIC PLAYER HIDDEN", $"{(m.Title.Length > 0 ? m.Title : "Music")} is playing. {Gesture("media.toggleui")} shows the player on the overlay."));
        };
        NotePadView.PopOut = n => { EngageIfNeeded(); bool on = _overlay.TogglePinnedNote(n); _overlay.ShowToast("NOTE", on ? "Pinned on the overlay · move it in customize mode" : "Taken off the overlay"); return on; };
        NotePadView.IsPoppedOut = n => _overlay.IsNotePinned(n);
        Edition.ShowUpgrade = ShowUpgrade;
        Edition.Changed += () => Dispatcher.BeginInvoke(() => { UpdateEditionBadge(); _overlay.Refresh(); RenderPage(); });
        _overlay.IsMainWindow = h => { var me = new System.Windows.Interop.WindowInteropHelper(this).Handle; return h == me || NativeMethods.GetAncestor(h, NativeMethods.GA_ROOTOWNER) == me; };
        _tick.Tick += (_, _) => OnTick();

        Loaded += (_, _) => Start();
        Closing += OnClosing;
        PreviewKeyDown += OnKeyDown;
        WindowShape.Attach(this, WindowFrame.Chamfer);
        StateChanged += (_, _) => { MaxButton.Content = WindowState == WindowState.Maximized ? "" : ""; ApplyMaximizedFrame(); };
        SizeChanged += (_, _) => { ResponsiveNav(); SaveGeometry(); };
        LocationChanged += (_, _) => SaveGeometry();
    }

    // ================================================================ startup / shutdown

    private void Start()
    {
        ApplyAppearance();
        UpdateSaveIndicator();
        if (DataStore.LoadWarning is { } w) ShowBanner(w, sticky: true);

        _hotkeys = new HotkeyManager(this);
        App.Keys = new KeybindHost(_hotkeys, Run);
        App.Keys.Rebuild();

        IntelService.Instance.Start();
        ChatWatch.Start(); // Pro: chat rules + the local AI spotting upcoming events (Free: no-op)
        DefconService.Instance.GameWindow = () => GameLocator.Find(Target);
        DefconService.Instance.GameInFront = () => _gameInFront;
        DefconService.Instance.Start();
        UpdateEditionBadge();
        StartTrialOffers();
        StartAnnouncements();
        LocalAi.Instance.RefreshInstalled();
        _ = MediaService.Instance.StartAsync();

        _fgHook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _fgHookProc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        _overlay.SetTarget(Target);
        if (S.AutoEngage) _overlay.Engage();
        OnForegroundChanged(IntPtr.Zero, 0, IntPtr.Zero, 0, 0, 0, 0);
        // Command line: --page <id> [--sub <tab>] opens a page; --customize opens the overlay menu.
        var args = Environment.GetCommandLineArgs();
        int pi = Array.FindIndex(args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        int si = Array.FindIndex(args, a => a.Equals("--sub", StringComparison.OrdinalIgnoreCase));
        if (si >= 0 && si + 1 < args.Length) _subTab = args[si + 1];
        Navigate(pi >= 0 && pi + 1 < args.Length ? args[pi + 1] : S.LastPage);
        if (args.Any(a => a.Equals("--customize", StringComparison.OrdinalIgnoreCase))) Dispatcher.BeginInvoke(() => _overlay.SetCustomize(true, focusGame: false), DispatcherPriority.ApplicationIdle);
        _tick.Start();
        OnTick();
        Log.Info($"started · {App.Keys.ArmedCount} keys armed, {App.Keys.ProblemCount} problems");
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closingForReal = true;
        _tick.Stop();
        if (_fgHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_fgHook);
        foreach (Window w in new Window?[] { _quickNote, _timerPrompt, _padWindow, _aiWindow, _companion }.Where(w => w is not null)!) w.Close();
        VideoHub.StopAll();
        DataStore.SaveNow();
        _hotkeys?.Dispose();
        _overlay.Close();
        IntelService.Instance.Stop();
        DefconService.Instance.Stop();
        LocalAi.Instance.Shutdown();
    }

    private void RestoreGeometry()
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Clamp(S.MainW, MinWidth, wa.Width);
        Height = Math.Clamp(S.MainH, MinHeight, wa.Height);
        if (!double.IsNaN(S.MainX) && !double.IsNaN(S.MainY))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = Math.Clamp(S.MainX, wa.Left, wa.Right - 200);
            Top = Math.Clamp(S.MainY, wa.Top, wa.Bottom - 120);
        }
    }

    private void SaveGeometry()
    {
        if (!IsLoaded || WindowState != WindowState.Normal || _closingForReal) return;
        double w = Math.Round(ActualWidth), h = Math.Round(ActualHeight), x = Math.Round(Left), y = Math.Round(Top);
        if (w == S.MainW && h == S.MainH && x == S.MainX && y == S.MainY) return;
        S.MainW = w; S.MainH = h; S.MainX = x; S.MainY = y;
        // Moving / resizing the window is saved quietly after you let go, not on every pixel.
        DataStore.MarkDirtyQuiet();
    }

    private void OnContextChanged()
    {
        ThemeManager.Apply();
        App.Keys?.Rebuild();
        _overlay.Refresh();
        UpdatePresetBadge();
        RefreshPageSoon();
    }

    // ================================================================ appearance

    private void ApplyAppearance()
    {
        var s = S;
        ApplyScale(force: true);
        Root.Opacity = Math.Clamp(s.WindowOpacity, 0.6, 1);
        try
        {
            BackgroundImage.Source = s.BackgroundImage.Length > 0 && System.IO.File.Exists(s.BackgroundImage) ? new BitmapImage(new Uri(s.BackgroundImage)) : null;
        }
        catch (Exception) { BackgroundImage.Source = null; }
        BackgroundImage.Opacity = Math.Clamp(s.BackgroundImageOpacity, 0.02, 0.8);
        App.ReducedMotion = s.ReducedMotion || !SystemParameters.ClientAreaAnimation;
        ResponsiveNav();
        UpdatePresetBadge();
        ApplyMaximizedFrame();
    }

    /// <summary>
    /// A maximized borderless window hangs past the screen by the resize border, which clipped the
    /// frame and title bar. Pad it back in, and drop the cut corners (the screen edge is the frame then).
    /// </summary>
    private void ApplyMaximizedFrame()
    {
        bool max = WindowState == WindowState.Maximized;
        var b = SystemParameters.WindowResizeBorderThickness;
        Root.Margin = max ? new Thickness(b.Left, b.Top, b.Right, b.Bottom) : new Thickness(0);
        WindowFrame.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdatePresetBadge()
    {
        PresetIcon.Text = DataStore.Preset.Icon;
        PresetName.Text = DataStore.Preset.Name.ToUpperInvariant();
        ProfileName.Text = "· " + DataStore.Profile.Name.ToUpperInvariant();
        TargetName.Text = Target.Name;
        TargetPreset.Text = $"{DataStore.Preset.Name.ToUpperInvariant()} · {DataStore.Profile.Name.ToUpperInvariant()}";
    }

    private void TargetBox_Click(object sender, MouseButtonEventArgs e) => PresetMenu(TargetBox);

    /// <summary>Numbered, upper-case navigation (the 0.3 look).</summary>
    private void BuildNav()
    {
        NavPanel.Children.Clear();
        _nav.Clear();
        int i = 1;
        foreach (var (id, label, tip) in NavItems)
        {
            var rb = new RadioButton { Content = NavContent(label, compact: false), Tag = i.ToString("00"), Style = Ui.Style("Sf.Nav"), GroupName = "MainNav", ToolTip = tip };
            string pid = id;
            rb.Checked += (_, _) => Navigate(pid);
            _nav[id] = rb;
            NavPanel.Children.Add(rb);
            i++;
        }
    }

    private double _uiScale = 1;

    /// <summary>
    /// Window UI scale = your "Window text scale" × an automatic factor for big windows. At 1600×1000 and
    /// below the factor is 1 (so 1080p and 4:3 screens keep the dense layout); a maximized 1440p window
    /// gets ~1.4×, 4K at 100% up to 2×, so text and cards stay readable instead of shrinking into a strip.
    /// </summary>
    private void ApplyScale(bool force = false)
    {
        double w = Math.Max(1, ActualWidth > 0 ? ActualWidth : Width), h = Math.Max(1, ActualHeight > 0 ? ActualHeight : Height);
        double auto = S.AutoScaleLargeWindows ? Math.Clamp(Math.Min(w / 1600, h / 1000), 1, 2) : 1;
        double scale = Math.Clamp(Math.Clamp(S.FontScale, 0.8, 1.5) * auto, 0.8, 2.4);
        if (!force && Math.Abs(scale - _uiScale) < 0.03) return;
        _uiScale = scale;
        Scaled.LayoutTransform = Math.Abs(scale - 1) < 0.01 ? null : new ScaleTransform(scale, scale);
    }

    /// <summary>Width the layout actually gets, in unscaled units (what the pages lay out against).</summary>
    private double EffectiveWidth => (ActualWidth > 0 ? ActualWidth : Width) / _uiScale;

    private void ResponsiveNav()
    {
        ApplyScale();
        // Title bar: drop the tagline before the search box gets squeezed; hide the search hint when tiny.
        TagLine.Visibility = EffectiveWidth < 1120 ? Visibility.Collapsed : Visibility.Visible;
        SearchHint.Text = EffectiveWidth < 860 ? "Search" : "Search everything   Ctrl+F";
        bool compact = S.CompactMode || EffectiveWidth < 900;
        NavColumn.Width = new GridLength(compact ? 64 : 210);
        CollapseNav.Content = compact ? "" : "";
        TargetBox.Visibility = NavCaption.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        int i = 1;
        foreach (var (id, label, tip) in NavItems)
        {
            if (!_nav.TryGetValue(id, out var rb)) continue;
            // Compact: emoji only (the number and label would be clipped); the tooltip still names the page.
            rb.Content = NavContent(label, compact);
            rb.Tag = compact ? null : i.ToString("00");
            rb.ToolTip = compact ? $"{label} · {tip}" : tip;
            i++;
        }
    }

    private static object NavContent(string label, bool compact)
    {
        var emoji = Ui.Emoji(Ui.EmojiFor(label.ToUpperInvariant()), compact ? 16 : 13);
        emoji.Margin = new Thickness(0, 0, compact ? 0 : 10, 0);
        emoji.Width = compact ? double.NaN : 18;
        emoji.TextAlignment = TextAlignment.Center;
        if (compact) return emoji;
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(emoji);
        sp.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    private void CollapseNav_Click(object sender, RoutedEventArgs e)
    {
        S.CompactMode = !S.CompactMode;
        DataStore.MarkDirty();
        ResponsiveNav();
    }

    public void Navigate(string page, string? sub = null)
    {
        if (NavItems.All(n => n.Id != page)) page = "home";
        if (sub is not null) _subTab = sub;
        if (_page != page || PageHost.Content is null)
        {
            if (PageHost.Content is FrameworkElement old) FlushPage(old);
            _page = page;
            S.LastPage = page;
            DataStore.MarkDirty();
        }
        if (_nav.TryGetValue(page, out var rb) && rb.IsChecked != true) { rb.IsChecked = true; return; }
        RenderPage();
        PageScroll.ScrollToTop();
    }

    private static void FlushPage(FrameworkElement old)
    {
        foreach (var v in FindChildren<NotePadView>(old)) v.FlushDraft();
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in FindChildren<T>(c)) yield return x;
        }
    }

    private bool _refreshQueued;

    /// <summary>Rebuilds the current page once, after the current burst of changes.</summary>
    private void RefreshPageSoon()
    {
        if (_refreshQueued) return;
        // Don't rebuild under the user's cursor while they're typing in this page.
        if (Keyboard.FocusedElement is TextBox tb && IsAncestorOf(tb)) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() => { _refreshQueued = false; RenderPage(); }, DispatcherPriority.Background);
    }

    private void RenderPage()
    {
        double offset = PageScroll.VerticalOffset;
        int idx = Array.FindIndex(NavItems, n => n.Id == _page);
        Eyebrow = $"{idx + 1:00} // {NavItems[Math.Max(0, idx)].Label.ToUpperInvariant()}";
        FrameworkElement content = _page switch
        {
            "notes" => NotesPage(),
            "overlay" => OverlayPage(),
            "defcon" => DefconPage(),
            "timers" => TimersPage(),
            "ai" => AiPage(),
            "presets" => PresetsPage(),
            "games" => GamesPage(),
            "settings" => SettingsPage(),
            "pro" => ProPage(),
            _ => HomePage(),
        };
        PageHost.Content = content;
        PageScroll.ScrollToVerticalOffset(offset);
        UpdateFooter();
    }

    // ================================================================ commands (keybinds)

    public void Run(string action)
    {
        Log.Info("action " + action);
        var p = DataStore.Preset;
        switch (action)
        {
            case "overlay.toggle": if (_overlay.Engaged) _overlay.Disengage(); else EngageOverlay(false); break;
            case "overlay.customize": _overlay.ToggleCustomize(); break;
            case "overlay.settings": _overlay.OpenMenu("DISPLAY"); break;
            case "overlay.crosshair": S.CrosshairEnabled = true; _overlay.OpenMenu("CROSSHAIR"); break;
            case "overlay.crosshair.toggle": S.CrosshairEnabled = !S.CrosshairEnabled; _overlay.ApplyCrosshair(); DataStore.MarkDirty(); break;
            case "overlay.panic": PanicHide(); break;
            case "overlay.distraction": S.DistractionFree = !S.DistractionFree; _overlay.UpdateVisibility(); _overlay.ShowToast("DISTRACTION-FREE", S.DistractionFree ? "On · only crosshair and toasts" : "Off"); DataStore.MarkDirty(); break;
            case "overlay.cards": S.PinsVisible = !S.PinsVisible; _overlay.UpdateVisibility(); DataStore.MarkDirty(); break;
            case "overlay.page.next": _overlay.StepPage(+1); break;
            case "overlay.page.prev": _overlay.StepPage(-1); break;
            case "overlay.reset": _overlay.ResetPositions(); break;
            case "dashboard.open": BringToFront(); break;
            case "note.quick": if (CanPopUp()) OpenQuickNote(null); break;
            case "pad.window": if (CanPopUp()) TogglePads(null); break;
            case "pad.next": Pads.Step(+1); _overlay.ShowToast("NOTE PAD", Pads.Current.Title); break;
            case "pad.prev": Pads.Step(-1); _overlay.ShowToast("NOTE PAD", Pads.Current.Title); break;
            case "pad.clear": ClearCurrentPad(); break;
            case "note.paste": QuickPaste(); break;
            case "timer.quick": StartTimer(TimeSpan.FromMinutes(Math.Max(1, S.QuickTimerMinutes)), null); break;
            case "timer.manual": if (CanPopUp()) OpenTimerPrompt(); break;
            case "timer.stopwatch": TimerService.Instance.StartStopwatch(); EnsureTimersVisible(); break;
            case "timers.toggle": S.TimersVisible = !S.TimersVisible; _overlay.UpdateVisibility(); DataStore.MarkDirty(); break;
            case "timers.clear": TimerService.Instance.CancelAll(); break;
            case "defcon.up": DefconService.Instance.Step(-1); break;
            case "defcon.down": DefconService.Instance.Step(+1); break;
            case "defcon.accept": DefconService.Instance.Accept(); break;
            case "defcon.dismiss": DefconService.Instance.Dismiss(); break;
            case "defcon.scan": _ = ScanDefcon(); break;
            case "defcon.default": DefconService.Instance.ResetToDefault(); _overlay.ShowToast("DEFCON", $"Back to default · {DefconService.Instance.Active?.Label ?? "none"}"); break;
            case "ai.open": if (CanPopUp()) OpenAi(); break;
            case "media.playpause": _ = MediaService.Instance.PlayPauseAsync(); break;
            case "media.next": _ = MediaService.Instance.NextAsync(); break;
            case "media.prev": _ = MediaService.Instance.PrevAsync(); break;
            case "media.toggleui": ToggleMediaUi(); break;
            case "video.playpause": if (VideoHub.Current is { } vp) { vp.Toggle(); _overlay.ShowToast("VIDEO", vp.Playing ? "Playing" : "Paused"); } else _overlay.ShowToast("VIDEO", "No video on this page. Add a Video element in customize mode."); break;
            default:
                if (action.StartsWith("pad.open.") && p.Pad(action[9..]) is { } po) { if (CanPopUp()) TogglePads(po); }
                else if (action.StartsWith("pad.add.") && p.Pad(action[8..]) is { } pa) { if (CanPopUp()) OpenQuickNote(pa); }
                else if (action.StartsWith("page.")) { EngageIfNeeded(); _overlay.SwitchPage(action[5..], toast: true); }
                else if (action.StartsWith("defcon.")) DefconService.Instance.SetById(action[7..], "key");
                else if (action.StartsWith("element.")) _overlay.ToggleElement(action[8..]);
                else if (action.StartsWith("timer.") && DataStore.Profile.TimerPresets.FirstOrDefault(t => t.Id == action[6..]) is { } tp) { TimerService.Instance.Start(tp); EnsureTimersVisible(); _overlay.ShowToast("TIMER STARTED", tp.Name); }
                break;
        }
        UpdateFooter();
    }

    private async System.Threading.Tasks.Task ScanDefcon()
    {
        _overlay.ShowToast("DEFCON", "Scanning…");
        string r = await DefconService.Instance.ScanNowAsync();
        if (DefconService.Instance.Pending is null) _overlay.ShowToast("DEFCON SCAN", r);
    }

    private void EngageIfNeeded() { if (!_overlay.Engaged) EngageOverlay(false); }

    private void EnsureTimersVisible()
    {
        EngageIfNeeded();
        if (!DataStore.Preset.ActivePage.Elements.Any(e => e.Kind == ElementKind.Timers))
            _overlay.ShowToast("TIMER", "Add a Timers element to this page (Alt+~) to see it on screen.");
    }

    private bool _musicHintShown;

    private void ToggleMediaUi()
    {
        _overlay.SetMusicVisible(!S.MediaVisible);
        _overlay.ShowToast("MUSIC PLAYER", S.MediaVisible ? "Shown · " + MediaService.Instance.ModeLabel.ToLowerInvariant() : "Hidden");
    }

    private void PanicHide()
    {
        _overlay.Disengage();
        foreach (Window w in new Window?[] { _companion, _quickNote, _timerPrompt, _padWindow, _aiWindow }.Where(w => w is not null)!)
        {
            if (w == _companion) w.Hide(); else w.Close();
        }
        WindowState = WindowState.Minimized;
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true; Topmost = false;
    }

    private void EngageOverlay(bool focusGame)
    {
        _overlay.SetTarget(Target);
        _overlay.Engage();
        if (focusGame && GameLocator.IsRunning(Target))
        {
            WindowState = WindowState.Minimized;
            GameLocator.FocusTarget(Target);
        }
    }

    private void OpenQuickNote(NotePad? pad)
    {
        if (_quickNote is { IsLoaded: true }) { _quickNote.Activate(); return; }
        _quickNote = new QuickNoteWindow(Target, pad);
        _quickNote.Closed += (_, _) => _quickNote = null;
        _quickNote.Show();
    }

    /// <summary>The pad keys toggle: press again to close the window (and go back to the game).</summary>
    private void TogglePads(NotePad? pad)
    {
        if (_padWindow is { IsLoaded: true, IsVisible: true } w && (pad is null || w.CurrentPad.Id == pad.Id))
        {
            w.CloseToGame();
            return;
        }
        OpenPads(pad);
    }

    private void OpenPads(NotePad? pad)
    {
        if (_padWindow is not { IsLoaded: true })
        {
            _padWindow = new NotePadWindow(Target);
            _padWindow.Closed += (_, _) => _padWindow = null;
        }
        _padWindow.ShowPad(pad ?? Pads.Current);
    }

    private void ClearCurrentPad()
    {
        if (_padWindow is { IsLoaded: true }) { _padWindow.ClearCurrent(); return; }
        var pad = Pads.Current;
        if (pad.Notes.Count == 0) { _overlay.ShowToast("NOTE PAD", $"\"{pad.Title}\" is already empty."); return; }
        if (pad.ConfirmDelete && S.ConfirmDeletes && !Ui.Confirm(null, "Clear pad", $"Clear all {pad.Notes.Count} notes from \"{pad.Title}\"? Undo in the pad window brings them back.", "CLEAR")) return;
        int n = Pads.Clear(pad);
        _overlay.ShowToast("NOTE PAD CLEARED", $"{n} notes moved to the pad's undo list.");
    }

    private void QuickPaste()
    {
        if (!Clipboard.ContainsText()) { _overlay.ShowToast("QUICK PASTE", "The clipboard has no text."); return; }
        string text = Clipboard.GetText().Trim();
        if (text.Length == 0) return;
        Pads.Add(Pads.Current, text.Length > 20000 ? text[..20000] : text);
        _overlay.ShowToast("PASTED", $"Added to \"{Pads.Current.Title}\".");
    }

    private void OpenTimerPrompt()
    {
        if (_timerPrompt is { IsLoaded: true }) { _timerPrompt.Activate(); return; }
        _timerPrompt = new TimerPromptWindow(Target);
        _timerPrompt.Closed += (_, _) => _timerPrompt = null;
        _timerPrompt.Show();
    }

    private void OpenAi()
    {
        if (!Ui.RequirePro(ProFeature.LocalAi)) return;
        if (_aiWindow is { IsLoaded: true }) { _aiWindow.Activate(); return; }
        _aiWindow = new AiWindow(Target);
        _aiWindow.Closed += (_, _) => _aiWindow = null;
        _aiWindow.Show();
    }

    /// <summary>
    /// Like Discord's overlay: SFHud's in-game pop-ups (pads, quick note, AI, timer prompt) belong to the
    /// hooked game. They hide whenever you leave it (alt-tab, desktop, game closed) and come back, without
    /// taking focus, when the game is in front again. Their keys only open them over the game or SFHud.
    /// </summary>
    private void ParkPopups()
    {
        if (!S.PopupsFollowGame || _closingForReal) return;
        foreach (Window? w in new Window?[] { _padWindow, _quickNote, _aiWindow, _timerPrompt })
            if (w is { IsVisible: true } && !_parked.Contains(w)) { _parked.Add(w); w.Hide(); }
    }

    private void UnparkPopups()
    {
        foreach (var w in _parked.ToList())
            if (w.IsLoaded && !_closingForReal) { w.ShowActivated = false; w.Show(); w.ShowActivated = true; }
        _parked.Clear();
    }

    /// <summary>An SFHud window (main window, pad, AI…) is the foreground window.</summary>
    private static bool OursInFront()
    {
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>In-game pop-ups open only over the hooked game, or from SFHud itself.</summary>
    private bool CanPopUp() => !S.PopupsFollowGame || _overlay.InGame || GameLocator.IsForeground(Target) || OursInFront();



    private void OpenCompanion()
    {
        if (!Ui.RequirePro(ProFeature.Companion)) return;
        _companion ??= new CompanionWindow();
        _companion.Show();
        _companion.Activate();
    }

    private void StartTimer(TimeSpan d, string? label)
    {
        var t = TimerService.Instance.Start(d, label);
        EnsureTimersVisible();
        _overlay.ShowToast("TIMER STARTED", $"{t.Label} · ends {t.EndsAt:HH:mm:ss}");
    }

    // ================================================================ events

    private void OnDefconChanged(DefconLevel? old, DefconLevel? now)
    {
        var levels = DataStore.Preset.Defcon.Levels;
        bool worse = now is not null && (old is null || levels.IndexOf(now) < levels.IndexOf(old));
        if (now is not null)
        {
            if (now.Toast) _overlay.ShowToast(now.Label, now.Description.Length > 0 ? now.Description : DefconService.Instance.ActiveSource, urgent: worse, now.Color);
            SoundService.Play(now.Sound.Length > 0 ? now.Sound : worse ? S.AlertSound : S.NotifySound);
            if (now.Flash) _overlay.FlashDefcon();
        }
        if (DefconLayouts.Target(old, now) is { } pageId) _overlay.SwitchPage(pageId);
        _overlay.UpdateVisibility();
        DataStore.NotifyNotes(); // DEFCON-scoped pads appear / disappear
        if (_page is "home" or "defcon") RefreshPageSoon();
    }

    private void OnDefconSuggestion(DefconSuggestion? s)
    {
        if (s is not null)
        {
            string key = App.Gesture("defcon.accept");
            _overlay.ShowToast($"DEFCON SUGGESTED · {s.Level.Label}", $"{s.Source} · {s.ConfidenceText} sure{(s.FromAi ? " (AI guess)" : "")}. {(key.Length > 0 ? key + " accepts." : "Accept it in the menu.")}", urgent: true, s.Level.Color);
            SoundService.Play(S.NotifySound);
        }
        if (_page is "home" or "defcon") RefreshPageSoon();
    }

    /// <summary>Foreground changed: arm in-game-only keys, leave customize mode if the user clicked away.</summary>
    private void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        bool ours = pid == (uint)Environment.ProcessId;
        IntPtr game = GameLocator.Find(Target);
        _gameInFront = game != IntPtr.Zero && fg == game;
        App.Keys?.SetScopedArmed(_gameInFront || (ours && _overlay.IsVisible));
        if (_overlay.Customizing && !ours && !App.ScreenshotMode) _overlay.SetCustomize(false, focusGame: false);
        _overlay.Nudge();
    }

    private void OnTick()
    {
        SessionTracker.Instance.Update(Target, S);
        UpdateFooter();
        if (_page == "home") UpdateHomeLive();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.S) { DataStore.SaveNow(); e.Handled = true; }
        else if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox && GameLocator.IsRunning(Target))
        {
            WindowState = WindowState.Minimized;
            GameLocator.FocusTarget(Target);
            e.Handled = true;
        }
    }

    // ================================================================ title bar

    private DispatcherTimer? _saveUiDelay;

    /// <summary>
    /// The LOCAL · SAVED badge. "SAVING…" only shows if a save is still pending after a moment, so typing
    /// or dragging a slider doesn't make it flicker between SAVING and SAVED on every change.
    /// </summary>
    private void UpdateSaveIndicator()
    {
        if (DataStore.Dirty)
        {
            if (_saveUiDelay is null)
            {
                _saveUiDelay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1500) };
                _saveUiDelay.Tick += (_, _) => { _saveUiDelay!.Stop(); if (DataStore.Dirty) ShowSaving(); };
            }
            if (!_saveUiDelay.IsEnabled) _saveUiDelay.Start();
            return;
        }
        _saveUiDelay?.Stop();
        ShowSaved();
    }

    private void ShowSaving()
    {
        {
            SaveGlyph.Text = "●";
            SaveGlyph.SetResourceReference(TextBlock.ForegroundProperty, "B.Amber");
            SaveText.Text = "SAVING…"; SaveText.SetResourceReference(TextBlock.ForegroundProperty, "B.Amber");
        }
    }

    private void ShowSaved()
    {
        {
            SaveGlyph.Text = "●";
            SaveGlyph.SetResourceReference(TextBlock.ForegroundProperty, "B.Ok");
            SaveText.Text = DataStore.LastSaved == default ? "SAVED" : $"SAVED {DataStore.LastSaved:HH:mm}"; SaveText.SetResourceReference(TextBlock.ForegroundProperty, "B.Ok");
        }
    }

    private void SaveNow_Click(object sender, RoutedEventArgs e)
    {
        bool ok = DataStore.SaveNow();
        if (!ok) ShowBanner("Saving failed. Check that " + SettingsStore.DataDir + " is writable; details are in the log.", sticky: true);
    }

    private void Pro_Click(object sender, RoutedEventArgs e) => ShowUpgrade(null);

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn("open url: " + ex.Message); }
    }

    private void PresetBadge_Click(object sender, RoutedEventArgs e) => PresetMenu(PresetBadge);

    private void PresetMenu(UIElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        var game = new MenuItem { Header = "Change game…" };
        game.Click += (_, _) => Navigate("games");
        m.Items.Add(game);
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = "SERVER PRESET", IsEnabled = false });
        foreach (var p in DataStore.Profile.Presets)
        {
            var mi = new MenuItem { Header = (p.Id == DataStore.Preset.Id ? "✓  " : "     ") + p.Name };
            var pp = p;
            mi.Click += (_, _) => DataStore.SwitchPreset(pp);
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        m.Items.Add(new MenuItem { Header = "PROFILE", IsEnabled = false });
        foreach (var p in DataStore.Data.Profiles)
        {
            var mi = new MenuItem { Header = (p.Id == DataStore.Profile.Id ? "✓  " : "     ") + p.Name };
            var pp = p;
            mi.Click += (_, _) => { if (!Edition.ProfileUsable(DataStore.Data.Profiles.IndexOf(pp))) { Ui.RequirePro(ProFeature.UnlimitedProfiles); return; } DataStore.SwitchProfile(pp); };
            m.Items.Add(mi);
        }
        m.Items.Add(new Separator());
        var manage = new MenuItem { Header = "Manage presets and profiles…" };
        manage.Click += (_, _) => Navigate("presets");
        m.Items.Add(manage);
        m.IsOpen = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private System.Windows.Threading.DispatcherTimer? _bannerTimer;

    /// <summary>Shows a message above the page. Short results fade out on their own; warnings (sticky) stay until dismissed.</summary>
    private void ShowBanner(string text, bool sticky = false)
    {
        BannerText.Text = text.Trim();
        Banner.Visibility = Visibility.Visible;
        _bannerTimer?.Stop();
        if (sticky) return;
        _bannerTimer ??= new System.Windows.Threading.DispatcherTimer();
        _bannerTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(4 + text.Length / 20.0, 6, 16));
        _bannerTimer.Tick -= BannerTimeout;
        _bannerTimer.Tick += BannerTimeout;
        _bannerTimer.Start();
    }

    private void BannerTimeout(object? sender, EventArgs e)
    {
        _bannerTimer?.Stop();
        if (DataStore.LoadWarning is null) Banner.Visibility = Visibility.Collapsed;
    }

    private void Banner_Close(object sender, RoutedEventArgs e)
    {
        Banner.Visibility = Visibility.Collapsed;
        DataStore.LoadWarning = null;
    }

    // ================================================================ footer

    private void UpdateFooter()
    {
        var lvl = DefconService.Instance.Active;
        string keys = App.Keys is null ? "" : App.Keys.ProblemCount > 0 ? $"KEYS {App.Keys.ArmedCount} · {App.Keys.ProblemCount} CONFLICT" : $"KEYS {App.Keys.ArmedCount} READY";
        FooterText.Text = _overlay.Customizing
            ? "CUSTOMIZING  ·  DRAG ELEMENTS  ·  CORNERS RESIZE  ·  ESC SAVES AND CLOSES"
            : $"OVERLAY {_overlay.State}  ·  {keys}{(lvl is null ? "" : "  ·  " + lvl.Label.ToUpperInvariant())}  ·  {Gesture("overlay.customize")} CUSTOMIZE  ·  {Gesture("pad.window")} PADS  ·  {Gesture("ai.open")} AI";
        FooterRight.Text = $"v{AppInfo.Version}  ·  {DateTime.Now:HH:mm}";
        FooterDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, App.Keys?.ProblemCount > 0 ? "B.Amber" : _overlay.Engaged ? "B.Ok" : "B.Muted");
    }

    private static string Gesture(string a) { string g = App.Gesture(a); return g.Length == 0 ? "—" : g.ToUpperInvariant(); }
}
