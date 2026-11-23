using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SFHud.Controls;
using SFHud.Interop;
using SFHud.Services;
using static SFHud.Interop.NativeMethods;

namespace SFHud.Views;

/// <summary>
/// Transparent, click-through, non-activating window laid over the target game's client area.
/// Draws the active overlay page's elements. Alt+~ switches to customize mode: the overlay takes
/// the mouse, elements can be dragged, resized, restyled, added and removed, pages managed, and
/// the crosshair / DEFCON / display settings changed, all without leaving the game.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double M = 16;            // margin from the game's edges, in reference units
    private const double SnapDist = 6;

    private readonly DispatcherTimer _track = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _second = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _undoDebounce = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Dictionary<string, ElementView> _views = new();
    private IntPtr _hwnd;
    private RECT _lastRect;
    private GameTarget _target = GameCatalog.BuiltIn[0];
    private double _scale = 1;

    // customize state
    private OverlayElement? _selected;
    private Border? _selBox;
    private readonly List<Rectangle> _handles = new();
    private string _snapshot = "";
    private readonly List<string> _undo = new();
    private readonly List<string> _redo = new();
    private string _menuTab = "ELEMENTS";
    private enum DragKind { None, Move, Resize }
    private DragKind _drag;
    private int _corner; // 0 TL, 1 TR, 2 BR, 3 BL
    private Point _dragStart;
    private Rect _dragOrigin;
    private bool _dragMoved;
    private Action<Rect>? _regionDone;
    private Point _regionStart;

    public bool Engaged { get; private set; }
    public bool Customizing { get; private set; }
    public string State { get; private set; } = "STANDBY";
    public IntPtr GameHwnd => GameLocator.Find(_target);

    public event Action<string>? OverlayStateChanged;
    public event Action<bool>? CustomizeChanged;
    public event Action<string>? CommandRequested; // e.g. "ai.open", "pad.window", "dashboard.open"
    /// <summary>True while the overlay is up over the game; false when the game is unfocused, minimized or gone.</summary>
    public event Action<bool>? InGameChanged;
    /// <summary>True while another app (not the game, not SFHud) is in front of a running game.</summary>
    public event Action<bool>? OtherAppInFrontChanged;
    public bool InGame { get; private set; }
    public bool OtherAppInFront { get; private set; }

    private static AppSettings S => DataStore.Settings;
    private static ServerPreset P => DataStore.Preset;

    public OverlayWindow()
    {
        InitializeComponent();
        _winEventProc = OnWinEvent;
        Closed += (_, _) => Unhook();
        _track.Tick += (_, _) => Track();
        _second.Tick += (_, _) => { foreach (var v in _views.Values) v.Tick(); };
        _undoDebounce.Tick += (_, _) => { _undoDebounce.Stop(); PushUndo(); };
        SourceInitialized += (_, _) => { _hwnd = new WindowInteropHelper(this).Handle; SetClickThrough(true); ApplyCaptureAffinity(); };
        Stage.SizeChanged += (_, _) => Relayout();
        TopBar.SizeChanged += (_, _) => ApplyChromeScale();

        World.MouseMove += World_MouseMove;
        World.MouseLeftButtonUp += World_MouseUp;
        Stage.MouseLeftButtonDown += (_, e) => { if (Customizing && ReferenceEquals(e.OriginalSource, Stage)) Select(null); };
        Backdrop.MouseLeftButtonDown += (_, _) => { if (Customizing) Select(null); };
        KeyDown += OnKey;

        RegionLayer.MouseLeftButtonDown += Region_Down;
        RegionLayer.MouseMove += Region_Move;
        RegionLayer.MouseLeftButtonUp += Region_Up;

        DataStore.NotesChanged += () => { Rebuild(ElementKind.NotePad); Rebuild(ElementKind.Note); UpdateVisibility(); };
        VideoHub.Changed += () => { if (Customizing) Dispatcher.BeginInvoke(() => Rebuild(ElementKind.Video), DispatcherPriority.Background); };
        DataStore.ContextChanged += () => { _undo.Clear(); _redo.Clear(); Select(null); Refresh(); };
        ThemeManager.Changed += Refresh;
        IntelService.Instance.Changed += () => Dispatcher.BeginInvoke(() => Rebuild(ElementKind.Intel), DispatcherPriority.Background);
        MediaService.Instance.Changed += () => Rebuild(ElementKind.Media);
        DefconService.Instance.PropertyChanged += (_, _) => { Rebuild(ElementKind.Defcon); UpdateVisibility(); ApplyOpacity(); };
        TimerService.Instance.Timers.CollectionChanged += (_, _) => { Rebuild(ElementKind.Timers); UpdateVisibility(); };
    }

    // ================================================================ window style

    private void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        if (on) ex |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        else ex &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
    }

    public void ApplyCaptureAffinity()
    {
        if (_hwnd != IntPtr.Zero) SetWindowDisplayAffinity(_hwnd, S.ExcludeFromCapture ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    public void SetTarget(GameTarget t)
    {
        if (_target.Id != t.Id) _lastRect = default;
        _target = t;
    }

    public void Engage()
    {
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        Engaged = true;
        _track.Start();
        _second.Start();
        if (_views.Count == 0) Refresh();
        Track();
    }

    public void Disengage()
    {
        if (Customizing) SetCustomize(false, focusGame: false);
        Engaged = false;
        _track.Stop();
        _second.Stop();
        Unhook();
        HideOverlay();
        SetState("STANDBY");
        Report(false, false);
    }

    private void Report(bool inGame, bool otherApp)
    {
        if (inGame != InGame) { InGame = inGame; VideoHub.GameFocus(inGame); InGameChanged?.Invoke(inGame); if (inGame) QueueSettle(); }
        if (otherApp != OtherAppInFront) { OtherAppInFront = otherApp; OtherAppInFrontChanged?.Invoke(otherApp); }
    }

    /// <summary>
    /// The pad window's pop-out button: shows one note as its own element on the current overlay page
    /// (click-through, moves and scales like every other element). Pressing it again takes it off.
    /// Returns true if the note is now on the overlay.
    /// </summary>
    public bool TogglePinnedNote(PadNote note)
    {
        var page = P.ActivePage;
        if (page.Elements.FirstOrDefault(e => e.Kind == ElementKind.Note && e.NoteId == note.Id) is { } existing)
        {
            page.Elements.Remove(existing);
            if (ReferenceEquals(_selected, existing)) Select(null);
            PushUndo();
            Refresh();
            return false;
        }
        // Stack new pinned notes down the left side so several don't land on top of each other.
        int n = page.Elements.Count(e => e.Kind == ElementKind.Note);
        var el = ElementCatalog.Create(ElementKind.Note, 0.02 + 0.02 * (n % 4), Math.Min(0.85, 0.18 + 0.16 * (n % 5)));
        el.NoteId = note.Id;
        page.Elements.Add(el);
        PushUndo();
        Refresh();
        return true;
    }

    public bool IsNotePinned(PadNote note) => P.ActivePage.Elements.Any(e => e.Kind == ElementKind.Note && e.NoteId == note.Id);

    public void Nudge()
    {
        if (Engaged) Track();
    }

    // ================================================================ building

    private ElementContext Ctx => new()
    {
        Interactive = Customizing,
        HostsVideo = true,
        TargetName = () => _target.IsAuto ? (GameLocator.AutoTargetName() ?? "ANY GAME") : _target.Name,
        SessionText = () => SessionTracker.Instance.Running ? SessionTracker.Instance.SessionText : "--:--:--",
        GameAttached = () => State == "ATTACHED" || State == "CUSTOMIZING",
    };

    /// <summary>Redraws every element of the active page.</summary>
    public void Refresh()
    {
        World.Children.Clear();
        _views.Clear();
        _selBox = null;
        _handles.Clear();
        var ctx = Ctx;
        foreach (var el in ElementsToDraw())
        {
            var v = new ElementView(el, ctx);
            Hook(v);
            _views[el.Id] = v;
            World.Children.Add(v);
        }
        ApplyCrosshair();
        ApplyOpacity();
        UpdateVisibility();
        if (_selected is not null && !_views.ContainsKey(_selected.Id)) _selected = null;
        if (Customizing) { BuildAdorner(); BuildTopBar(); BuildMenu(); }
        Relayout();
    }

    /// <summary>
    /// The active page's elements, plus the music player when this page has none: in game the player follows
    /// you to every page (it's the same element as on its own page, so it keeps that position). In customize
    /// mode only the page's own elements are shown, so editing stays per page.
    /// </summary>
    private IEnumerable<OverlayElement> ElementsToDraw()
    {
        var own = P.ActivePage.Elements;
        foreach (var el in own) yield return el;
        if (Customizing || !S.MediaVisible || own.Any(e => e.Kind == ElementKind.Media)) yield break;
        var borrowed = P.Pages.SelectMany(p => p.Elements).FirstOrDefault(e => e.Kind == ElementKind.Media && e.Visible);
        if (borrowed is not null) yield return borrowed;
    }

    /// <summary>Redraws only elements of the given kind (notes changed, timers changed…).</summary>
    public void Rebuild(ElementKind kind)
    {
        if (!Engaged) return;
        foreach (var v in _views.Values.Where(v => v.El.Kind == kind)) v.Build();
        Dispatcher.BeginInvoke(Relayout, DispatcherPriority.Loaded);
    }

    private void RebuildOne(OverlayElement el)
    {
        if (_views.TryGetValue(el.Id, out var v)) v.Build();
        Dispatcher.BeginInvoke(() => { Relayout(); UpdateAdorner(); }, DispatcherPriority.Loaded);
    }

    private void Hook(ElementView v)
    {
        v.SizeChanged += (_, _) => { Position(v); if (ReferenceEquals(_selected, v.El)) UpdateAdorner(); if (v.El.HudClearedFor != _target.Id) QueueSettle(); };
        v.PreviewMouseLeftButtonDown += Element_MouseDown;
        v.MouseRightButtonUp += (_, e) => { if (Customizing) { Select(v.El); ElementMenu(v.El).IsOpen = true; e.Handled = true; } };
    }

    private bool ShouldShow(OverlayElement el)
    {
        if (Customizing) return true;
        if (!el.Visible || S.DistractionFree) return false;
        if (el.DefconFilter.Count > 0 && !el.DefconFilter.Contains(DefconService.Instance.Active?.Id ?? "")) return false;
        switch (el.Kind)
        {
            case ElementKind.InfoCard: return S.PinsVisible;
            case ElementKind.Timers: return S.TimersVisible && TimerService.Instance.Timers.Count > 0;
            case ElementKind.Media: return S.MediaVisible;
            case ElementKind.Note: return Pads.FindNote(el.NoteId) is not null;
            case ElementKind.Defcon: return DefconService.Enabled;
            case ElementKind.Intel:
                var intel = IntelService.Instance;
                return S.IntelEnabled && (intel.ActiveAlerts.Any() || intel.Orders.Length > 0 || intel.Summary.Length > 0);
            case ElementKind.NotePad:
                var pad = P.Pad(el.PadId);
                return pad is null || !Edition.Has(ProFeature.LevelLayouts) || pad.DefconLevelId.Length == 0 || pad.DefconLevelId == DefconService.Instance.Active?.Id;
        }
        return true;
    }

    public void UpdateVisibility()
    {
        foreach (var v in _views.Values)
        {
            bool show = ShouldShow(v.El);
            v.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            // Hidden-but-present elements are drawn faded while customizing.
            v.Opacity = Customizing && (!v.El.Visible || !RealVisible(v.El)) ? 0.35 : Math.Clamp(v.El.Opacity, 0.1, 1);
        }
    }

    private bool RealVisible(OverlayElement el)
    {
        bool c = Customizing;
        Customizing = false;
        bool r = ShouldShow(el);
        Customizing = c;
        return r;
    }

    private void ApplyOpacity()
    {
        var lvl = DefconService.Instance.Active;
        Root.Opacity = lvl is { OverlayOpacity: > 0 } ? lvl.OverlayOpacity : Math.Clamp(S.OverlayOpacity, 0.2, 1);
    }

    // ================================================================ layout

    private double WorldW => Stage.ActualWidth / _scale;
    private double WorldH => Stage.ActualHeight / _scale;

    private void Relayout()
    {
        if (Stage.ActualWidth <= 0) return;
        _scale = Math.Clamp(S.UiScale * (S.ScaleWithResolution ? ResolutionFactor(Stage.ActualWidth, Stage.ActualHeight, S.MinReadableScale) : 1), 0.4, 4);
        WorldScale.ScaleX = WorldScale.ScaleY = _scale;
        World.Width = WorldW;
        World.Height = WorldH;
        foreach (var v in _views.Values) Position(v);
        if (S.SnapToGrid && Customizing) DrawGrid();
        UpdateAdorner();
        ApplyChromeScale();
        if (Customizing) Dispatcher.BeginInvoke(ArrangeChrome, DispatcherPriority.Loaded);
        QueueSettle();
    }

    private bool _settleQueued;

    private void QueueSettle()
    {
        if (_settleQueued) return;
        _settleQueued = true;
        Dispatcher.BeginInvoke(() => { _settleQueued = false; SettleOnce(); }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// One time per element and game, on the real game window (not the preview): an element whose saved
    /// position is on the game's HUD is moved just clear of it, and of your other elements, and that
    /// position is saved. After that it's marked and never moves by itself again, in or out of the game.
    /// </summary>
    private void SettleOnce()
    {
        if (!Engaged || _lastRect.Width <= 0 || _drag != DragKind.None || WorldW <= 0) return;
        var zones = HudZones.For(_target.Id);
        if (zones.Count == 0) return;
        bool changed = false;
        var placed = _views.Values.Where(v => v.Visibility == Visibility.Visible && v.ActualWidth > 0).ToList();
        foreach (var v in placed)
        {
            if (v.El.HudClearedFor == _target.Id) continue;
            double w = v.ActualWidth, h = v.ActualHeight;
            var r = new Rect(Canvas.GetLeft(v), Canvas.GetTop(v), w, h);
            // Other elements count as zones too, so nothing is moved onto something else.
            var avoid = zones.Concat(placed.Where(o => !ReferenceEquals(o, v)).Select(o =>
                new HudZones.Zone("", new Rect(Canvas.GetLeft(o) / WorldW, Canvas.GetTop(o) / WorldH, o.ActualWidth / WorldW, o.ActualHeight / WorldH)))).ToList();
            var onHud = HudZones.Clear(r, new Rect(0, 0, WorldW, WorldH), zones, 8);
            if (Math.Abs(onHud.X - r.X) >= 0.5 || Math.Abs(onHud.Y - r.Y) >= 0.5)
            {
                var p = HudZones.Clear(r, new Rect(0, 0, WorldW, WorldH), avoid, 8, new Rect(M, M, Math.Max(1, WorldW - 2 * M), Math.Max(1, WorldH - 2 * M)));
                if (p == r.Location) p = onHud; // no spot clear of everything: at least get off the game's HUD
                SetFromRect(v.El, p.X, p.Y, w, h);
                Position(v);
            }
            v.El.HudClearedFor = _target.Id;
            changed = true;
        }
        if (changed) { DataStore.MarkDirty(); if (_selected is not null) UpdateAdorner(); }
    }

    /// <summary>
    /// 1080p = 1×. Height drives it (1440p → 1.33×, 4K → 2×), but narrow screens (4:3, 5:4) are also
    /// capped by width against a 1440-wide reference, so a layout built on 16:9 doesn't take a bigger
    /// share of a 4:3 screen and crowd the middle. <paramref name="floor"/> (Display › "Smallest text
    /// on low resolutions") keeps notes readable at CRT resolutions such as 800×600 and 1024×768.
    /// </summary>
    public static double ResolutionFactor(double w, double h, double floor = 0.8)
    {
        if (w <= 0 || h <= 0) return 1;
        return Math.Clamp(Math.Min(h / 1080.0, w / 1440.0), Math.Clamp(floor, 0.5, 1.2), 3);
    }

    /// <summary>
    /// The customize menu, top bar, status bar, toasts and region hint scale with the screen too:
    /// bigger on 1440p / 4K so they're readable from a distance, fitted on 4:3 / CRT screens so the
    /// menu never covers more than about half the game and the top bar wraps instead of running off.
    /// </summary>
    private void ApplyChromeScale()
    {
        double w = Stage.ActualWidth, h = Stage.ActualHeight;
        if (w <= 0 || h <= 0) return;
        double k = S.ScaleOverlayMenu ? Math.Clamp(Math.Min(h / 1080.0, w / 1440.0), 0.75, 2.5) : 1;
        var t = Math.Abs(k - 1) < 0.01 ? null : new ScaleTransform(k, k);
        foreach (var fe in new FrameworkElement[] { MenuHost, TopBar, StatusBar, Toasts, RegionHint }) fe.LayoutTransform = t;
        double avail = w / k;  // width the chrome lays out against, in its own units
        MenuHost.Width = Math.Clamp(avail * 0.48, 260, 360);
        TopBar.MaxWidth = Math.Max(300, avail - 24);
        Toasts.Width = Math.Min(400, avail - 32);
        StatusBar.MaxWidth = Math.Max(240, avail - 24);
        StatusText.TextTrimming = TextTrimming.CharacterEllipsis;
        // Keep the menu clear of the (possibly wrapped) top bar and status bar.
        double barBlock = (TopBar.ActualHeight + 52 + 14) * k + 12;
        if (_barOnTop)
        {
            TopBar.VerticalAlignment = StatusBar.VerticalAlignment = VerticalAlignment.Top;
            TopBar.Margin = new Thickness(0, 12, 0, 0);
            StatusBar.Margin = new Thickness(0, 12 + (TopBar.ActualHeight + 8) * k, 0, 0);
            MenuHost.Margin = new Thickness(12, Math.Max(110, barBlock), 12, 12);
        }
        else
        {
            TopBar.VerticalAlignment = StatusBar.VerticalAlignment = VerticalAlignment.Bottom;
            TopBar.Margin = new Thickness(0, 0, 0, 52 + _barLift);
            StatusBar.Margin = new Thickness(0, 0, 0, 14 + _barLift);
            MenuHost.Margin = new Thickness(12, 12, 12, Math.Max(110, barBlock + _barLift));
        }
    }

    private bool _barOnTop;
    private double _barLift;
    private bool _menuCollapsed;

    /// <summary>
    /// Customize mode: the editing chrome must never sit on the HUD you're editing. The bar moves to the
    /// top when elements are under it at the bottom, and (with Auto side) the menu takes the side that
    /// covers fewer elements.
    /// </summary>
    private void ArrangeChrome()
    {
        if (!Customizing) return;
        double w = Stage.ActualWidth, h = Stage.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var rects = _views.Values.Where(v => v.Visibility == Visibility.Visible && v.ActualWidth > 0)
            .Select(v => new Rect(Canvas.GetLeft(v) * _scale, Canvas.GetTop(v) * _scale, v.ActualWidth * _scale, v.ActualHeight * _scale)).ToList();
        double Overlap(Rect area) => rects.Sum(r => { var i = Rect.Intersect(r, area); return i.IsEmpty ? 0 : i.Width * i.Height; });

        double k = TopBar.LayoutTransform is ScaleTransform st ? st.ScaleX : 1;
        double barH = (TopBar.ActualHeight + StatusBar.ActualHeight + 80) * k;
        double barW = Math.Min(w, Math.Max(TopBar.ActualWidth, StatusBar.ActualWidth) * k + 24);
        var bottom = new Rect((w - barW) / 2, h - barH, barW, barH);
        var top = new Rect((w - barW) / 2, 0, barW, barH);
        bool onTop = Overlap(bottom) > 0 && Overlap(top) < Overlap(bottom);
        // Elements both above and below: stay at the bottom but lift the bar just over the low ones.
        double lift = 0;
        if (!onTop && Overlap(bottom) > 0)
        {
            double highest = rects.Where(r => r.IntersectsWith(bottom)).Min(r => r.Top);
            // The status line is the lowest piece (14 from the bottom); lift everything so it ends 8 above the element.
            lift = Math.Clamp(h - 14 - highest + 8, 0, h * 0.45);
        }
        if (Math.Abs(lift - _barLift) > 1) { _barLift = lift; ApplyChromeScale(); }

        if (S.MenuAutoSide)
        {
            double mw = MenuHost.ActualWidth * k + 24;
            bool left = Overlap(new Rect(0, 0, mw, h)) < Overlap(new Rect(w - mw, 0, mw, h));
            if (left != S.MenuOnLeft) { S.MenuOnLeft = left; MenuHost.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right; }
        }
        if (onTop != _barOnTop) { _barOnTop = onTop; ApplyChromeScale(); }
    }

    private void Position(ElementView v)
    {
        // Never wider / taller than the screen: a 16:9 layout's wide panels still fit a 4:3 or 5:4 game.
        v.MaxWidth = Math.Max(80, WorldW - 2 * M);
        v.MaxHeight = Math.Max(40, WorldH - 2 * M);
        double w = v.ActualWidth, h = v.ActualHeight;
        double freeX = Math.Max(0, WorldW - 2 * M - w), freeY = Math.Max(0, WorldH - 2 * M - h);
        // Always exactly the saved position. Nothing is pushed around at runtime: going in and out of the
        // game, the preview, a resized window or an element's content changing never moves it somewhere
        // else (that used to push elements onto each other). Keeping clear of the game's HUD is applied
        // once, when you drop an element in customize mode, and saved (see KeepClearOfGameHud).
        double left = M + Math.Clamp(v.El.X, 0, 1) * freeX, top = M + Math.Clamp(v.El.Y, 0, 1) * freeY;
        Canvas.SetLeft(v, Math.Round(left));
        Canvas.SetTop(v, Math.Round(top));
        if (Customizing) DrawHudZones();
    }

    /// <summary>Customize mode: the game's HUD areas as amber hatched boxes, so you can see why elements keep clear of them.</summary>
    private void DrawHudZones()
    {
        if (_zonesDrawnFor == (WorldW, WorldH, _target.Id, S.AvoidGameHud)) return;
        _zonesDrawnFor = (WorldW, WorldH, _target.Id, S.AvoidGameHud);
        foreach (var old in Guides.Children.OfType<FrameworkElement>().Where(c => Equals(c.Tag, "hudzone")).ToList()) Guides.Children.Remove(old);
        if (!Customizing) return;
        var amber = ThemeManager.Brush("B.Amber");
        foreach (var z in HudZones.For(_target.Id))
        {
            var box = new Border
            {
                Tag = "hudzone", Width = z.Area.Width * Stage.ActualWidth, Height = z.Area.Height * Stage.ActualHeight,
                BorderBrush = amber, BorderThickness = new Thickness(1), Opacity = 0.75, IsHitTestVisible = false,
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xC4, 0x00)),
                Child = new TextBlock { Text = "GAME HUD · " + z.Name, Foreground = amber, FontSize = 10, Margin = new Thickness(6, 4, 6, 4), FontFamily = (FontFamily)Application.Current.Resources["F.Mono"] },
            };
            Canvas.SetLeft(box, z.Area.X * Stage.ActualWidth);
            Canvas.SetTop(box, z.Area.Y * Stage.ActualHeight);
            Guides.Children.Insert(0, box);
        }
    }

    private (double, double, string, bool) _zonesDrawnFor;

    /// <summary>Removes snap guide lines but keeps the game-HUD boxes.</summary>
    private void ClearGuideLines()
    {
        foreach (var c in Guides.Children.OfType<FrameworkElement>().Where(c => !Equals(c.Tag, "hudzone")).ToList()) Guides.Children.Remove(c);
    }

    /// <summary>Moves an element out of the game's HUD areas (if that option is on) and saves the new position.</summary>
    private void KeepClearOfGameHud(OverlayElement el)
    {
        if (!_views.TryGetValue(el.Id, out var v) || v.ActualWidth <= 0 || WorldW <= 0) return;
        var zones = HudZones.For(_target.Id);
        if (zones.Count == 0) return;
        double w = v.ActualWidth, h = v.ActualHeight;
        var r = new Rect(Canvas.GetLeft(v), Canvas.GetTop(v), w, h);
        var p = HudZones.Clear(r, new Rect(0, 0, WorldW, WorldH), zones, 8, new Rect(M, M, Math.Max(1, WorldW - 2 * M), Math.Max(1, WorldH - 2 * M)));
        if (Math.Abs(p.X - r.X) < 0.5 && Math.Abs(p.Y - r.Y) < 0.5) return;
        SetFromRect(el, p.X, p.Y, w, h);
        Position(v);
        UpdateAdorner();
        ShowToast("GAME HUD", "Moved just clear of the game's HUD. Hold Alt while dropping to place it anyway.");
    }

    private void SetFromRect(OverlayElement el, double left, double top, double w, double h)
    {
        double freeX = Math.Max(1, WorldW - 2 * M - w), freeY = Math.Max(1, WorldH - 2 * M - h);
        el.X = Math.Round(Math.Clamp((left - M) / freeX, 0, 1), 4);
        el.Y = Math.Round(Math.Clamp((top - M) / freeY, 0, 1), 4);
    }

    private void DrawGrid()
    {
        double g = Math.Max(4, S.GridSize) * _scale;
        var brush = new DrawingBrush(new GeometryDrawing(ThemeManager.Brush("B.Accent"), null, new RectangleGeometry(new Rect(0, 0, 1, 1))))
        {
            TileMode = TileMode.Tile, Viewport = new Rect(0, 0, g, g), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, g, g), ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.None,
        };
        GridOverlay.Fill = brush;
        GridOverlay.Opacity = 0.35;
    }

    // ================================================================ customize mode

    public void ToggleCustomize() => SetCustomize(!Customizing);

    /// <summary>Customize mode and the region picker need the overlay up even if the game lost focus.</summary>
    private void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (_hwnd == IntPtr.Zero) _hwnd = new WindowInteropHelper(this).Handle;
        Activate();
    }

    public void SetCustomize(bool on, bool focusGame = true, string? tab = null)
    {
        if (tab is not null) _menuTab = tab;
        if (Customizing == on) { if (on) { BuildMenu(); ShowAndActivate(); } return; }
        if (on && !Engaged) Engage();
        Customizing = on;
        SetClickThrough(!on);
        IsHitTestVisible = on;
        Focusable = on;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        Backdrop.Visibility = TopBar.Visibility = MenuHost.Visibility = StatusBar.Visibility = vis;
        GridOverlay.Visibility = on && S.SnapToGrid ? Visibility.Visible : Visibility.Collapsed;
        if (on)
        {
            _snapshot = Snapshot();
            _undo.Clear();
            _redo.Clear();
            _undo.Add(PageJson());
            Refresh();
            Track();
            ShowAndActivate();
            Focus();
            Status("Drag to move · corners resize · right-click for options · Ctrl+Z undo · Esc saves and closes");
        }
        else
        {
            Select(null);
            Guides.Children.Clear();
            _zonesDrawnFor = default;
            _drag = DragKind.None;
            DataStore.SaveNow();
            Refresh();
            if (focusGame) GameLocator.FocusTarget(_target);
        }
        CustomizeChanged?.Invoke(on);
    }

    private void Status(string text) => StatusText.Text = text;

    private string Snapshot() => JsonSerializer.Serialize(new { P.Pages, P.ActivePageId, P.DefaultPageId, P.Layouts }, SettingsStore.Json);

    private void Revert()
    {
        try
        {
            var doc = JsonDocument.Parse(_snapshot).RootElement;
            P.Pages = doc.GetProperty("Pages").Deserialize<List<OverlayPage>>(SettingsStore.Json)!;
            P.ActivePageId = doc.GetProperty("ActivePageId").GetString() ?? "";
            P.DefaultPageId = doc.GetProperty("DefaultPageId").GetString() ?? "";
            P.Layouts = doc.GetProperty("Layouts").Deserialize<List<LayoutSnapshot>>(SettingsStore.Json)!;
            DataStore.NormalizePreset(P);
            DataStore.MarkDirty();
            _undo.Clear(); _redo.Clear(); _undo.Add(PageJson());
            Select(null);
            Refresh();
            ShowToast("REVERTED", "Layout is back to how it was when you opened the menu.");
        }
        catch (Exception ex) { Log.Error("revert", ex); }
    }

    private void Apply()
    {
        DataStore.SaveNow();
        _snapshot = Snapshot();
        ShowToast("SAVED", "Layout saved.");
    }

    // ================================================================ undo / redo

    private string PageJson() => JsonSerializer.Serialize(P.ActivePage, SettingsStore.Json);

    private void PushUndo()
    {
        string now = PageJson();
        if (_undo.Count > 0 && _undo[^1] == now) return;
        _undo.Add(now);
        if (_undo.Count > 80) _undo.RemoveAt(0);
        _redo.Clear();
        DataStore.MarkDirty();
        BuildTopBar();
    }

    private void Changed(OverlayElement el)
    {
        RebuildOne(el);
        _undoDebounce.Stop();
        _undoDebounce.Start();
    }

    private void RestorePage(string json)
    {
        var page = JsonSerializer.Deserialize<OverlayPage>(json, SettingsStore.Json);
        if (page is null) return;
        var cur = P.ActivePage;
        cur.Name = page.Name;
        cur.Elements = page.Elements;
        string? sel = _selected?.Id;
        _selected = cur.Elements.FirstOrDefault(e => e.Id == sel);
        DataStore.MarkDirty();
        Refresh();
    }

    private void Undo()
    {
        if (_undo.Count < 2) return;
        _redo.Add(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        RestorePage(_undo[^1]);
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        RestorePage(_undo[^1]);
    }

    // ================================================================ selection + adorner

    private void Select(OverlayElement? el)
    {
        _selected = el;
        BuildAdorner();
        if (Customizing)
        {
            if (el is not null && _menuTab is "ELEMENTS") _menuTab = "EDIT";
            BuildMenu();
        }
    }

    private void BuildAdorner()
    {
        if (_selBox is not null) World.Children.Remove(_selBox);
        foreach (var h in _handles) World.Children.Remove(h);
        _handles.Clear();
        _selBox = null;
        if (!Customizing || _selected is null || !_views.ContainsKey(_selected.Id)) return;

        _selBox = new Border { BorderThickness = new Thickness(1.5), IsHitTestVisible = false, CornerRadius = new CornerRadius(2) };
        _selBox.SetResourceReference(Border.BorderBrushProperty, "B.Amber");
        var tag = new Border { Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(-1, -20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        tag.SetResourceReference(Border.BackgroundProperty, "B.Amber");
        var tt = new TextBlock { Text = _selected.DisplayName.ToUpperInvariant() + (S.LayoutLocked ? "  ·  LOCKED" : ""), FontSize = 10 };
        tt.SetResourceReference(TextBlock.FontFamilyProperty, "F.Mono");
        tt.SetResourceReference(TextBlock.ForegroundProperty, "B.Bg");
        tag.Child = tt;
        _selBox.Child = new Grid { Children = { tag } };
        Panel.SetZIndex(_selBox, 50);
        World.Children.Add(_selBox);
        if (!S.LayoutLocked)
        {
            for (int i = 0; i < 4; i++)
            {
                var h = new Rectangle { Width = 11, Height = 11, StrokeThickness = 1.5, Cursor = i is 0 or 2 ? Cursors.SizeNWSE : Cursors.SizeNESW, Tag = i };
                h.SetResourceReference(Shape.FillProperty, "B.Bg");
                h.SetResourceReference(Shape.StrokeProperty, "B.Amber");
                h.MouseLeftButtonDown += Handle_MouseDown;
                Panel.SetZIndex(h, 51);
                _handles.Add(h);
                World.Children.Add(h);
            }
        }
        UpdateAdorner();
    }

    private void UpdateAdorner()
    {
        if (_selBox is null || _selected is null || !_views.TryGetValue(_selected.Id, out var v)) return;
        double l = Canvas.GetLeft(v), t = Canvas.GetTop(v), w = v.ActualWidth, h = v.ActualHeight;
        if (double.IsNaN(l) || double.IsNaN(t)) return;
        Canvas.SetLeft(_selBox, l - 3); Canvas.SetTop(_selBox, t - 3);
        _selBox.Width = w + 6; _selBox.Height = h + 6;
        var pts = new[] { (l, t), (l + w, t), (l + w, t + h), (l, t + h) };
        for (int i = 0; i < _handles.Count; i++) { Canvas.SetLeft(_handles[i], pts[i].Item1 - 5.5); Canvas.SetTop(_handles[i], pts[i].Item2 - 5.5); }
    }

    private static bool FromInteractive(object source)
    {
        for (DependencyObject? d = source as DependencyObject; d is not null;)
        {
            if (d is ButtonBase or TextBox or Slider or ScrollBar or Hyperlink) return true;
            if (d is FrameworkElement fe && fe.Cursor == Cursors.Hand) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Element_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Customizing || sender is not ElementView v) return;
        if (FromInteractive(e.OriginalSource)) { if (!ReferenceEquals(_selected, v.El)) Select(v.El); return; }
        if (!ReferenceEquals(_selected, v.El)) Select(v.El);
        if (S.LayoutLocked) { e.Handled = true; return; }
        _drag = DragKind.Move;
        _dragMoved = false;
        _dragStart = e.GetPosition(World);
        _dragOrigin = new Rect(Canvas.GetLeft(v), Canvas.GetTop(v), v.ActualWidth, v.ActualHeight);
        World.CaptureMouse();
        e.Handled = true;
    }

    private void Handle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_selected is null || !_views.TryGetValue(_selected.Id, out var v)) return;
        _drag = DragKind.Resize;
        _dragMoved = false;
        _corner = (int)((FrameworkElement)sender).Tag;
        _dragStart = e.GetPosition(World);
        _dragOrigin = new Rect(Canvas.GetLeft(v), Canvas.GetTop(v), v.ActualWidth, v.ActualHeight);
        World.CaptureMouse();
        e.Handled = true;
    }

    private void World_MouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragKind.None || _selected is null || !_views.TryGetValue(_selected.Id, out var v) || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(World);
        double dx = now.X - _dragStart.X, dy = now.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 2) return;
        _dragMoved = true;
        var el = _selected;

        if (_drag == DragKind.Move)
        {
            double left = _dragOrigin.X + dx, top = _dragOrigin.Y + dy, w = _dragOrigin.Width, h = _dragOrigin.Height;
            if (S.SnapToGrid && !Keyboard.IsKeyDown(Key.LeftAlt)) { double g = Math.Max(2, S.GridSize); left = Math.Round(left / g) * g; top = Math.Round(top / g) * g; }
            (left, top) = SnapGuides(el, left, top, w, h);
            SetFromRect(el, left, top, w, h);
            Position(v);
            Status($"{el.DisplayName} · X {el.X:P0} · Y {el.Y:P0}   (hold Alt to move freely)");
        }
        else
        {
            double l = _dragOrigin.Left, t = _dragOrigin.Top, r = _dragOrigin.Right, b = _dragOrigin.Bottom;
            if (_corner is 0 or 3) l += dx; else r += dx;
            if (_corner is 0 or 1) t += dy; else b += dy;
            double w = Math.Clamp(r - l, 80, 1600), h = Math.Clamp(b - t, 40, 1400);
            if (_corner is 0 or 3) l = r - w;
            if (_corner is 0 or 1) t = b - h;
            if (S.SnapToGrid) { double g = Math.Max(2, S.GridSize); w = Math.Round(w / g) * g; if (Math.Abs(dy) > 4) h = Math.Round(h / g) * g; }
            el.W = Math.Round(w);
            if (Math.Abs(dy) > 4 || el.H > 0) el.H = Math.Round(h); // vertical drag fixes the height (long briefings scroll)
            v.Width = el.W;
            if (el.H > 0) v.Height = el.H;
            SetFromRect(el, l, t, el.W, el.H > 0 ? el.H : v.ActualHeight);
            Position(v);
            Status($"{el.DisplayName} · {el.W:0} × {(el.H > 0 ? el.H.ToString("0") : "auto")}");
        }
        UpdateAdorner();
    }

    private void World_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == DragKind.None) return;
        World.ReleaseMouseCapture();
        bool wasResize = _drag == DragKind.Resize;
        _drag = DragKind.None;
        ClearGuideLines();
        if (!_dragMoved) return;
        if (wasResize && _selected is not null) RebuildOne(_selected);
        // Dropped on the game's HUD (amber boxes)? Move it just clear and save that. Hold Alt to place it anyway.
        if (!wasResize && _selected is not null && (Keyboard.Modifiers & ModifierKeys.Alt) == 0) KeepClearOfGameHud(_selected);
        if (_selected is not null) _selected.HudClearedFor = _target.Id; // placed by hand: never moved by itself
        PushUndo();
        if (_menuTab == "EDIT") BuildMenu();
    }

    /// <summary>Snaps edges / centers to the screen center and other elements, drawing guide lines.</summary>
    private (double, double) SnapGuides(OverlayElement el, double left, double top, double w, double h)
    {
        ClearGuideLines();
        if (!S.ShowGuides || Keyboard.IsKeyDown(Key.LeftAlt)) return (left, top);
        var xs = new List<double> { M, WorldW / 2, WorldW - M };
        var ys = new List<double> { M, WorldH / 2, WorldH - M };
        foreach (var o in _views.Values.Where(o => o.El != el && o.Visibility == Visibility.Visible))
        {
            double ol = Canvas.GetLeft(o), ot = Canvas.GetTop(o);
            xs.AddRange(new[] { ol, ol + o.ActualWidth / 2, ol + o.ActualWidth });
            ys.AddRange(new[] { ot, ot + o.ActualHeight / 2, ot + o.ActualHeight });
        }
        double? gx = null, gy = null;
        foreach (var (edge, off) in new[] { (left, 0.0), (left + w / 2, w / 2), (left + w, w) })
        {
            var hit = xs.Select(x => (x, d: Math.Abs(x - edge))).Where(t => t.d < SnapDist).OrderBy(t => t.d).FirstOrDefault();
            if (hit.d < SnapDist && hit != default) { left = hit.x - off; gx = hit.x; break; }
        }
        foreach (var (edge, off) in new[] { (top, 0.0), (top + h / 2, h / 2), (top + h, h) })
        {
            var hit = ys.Select(y => (y, d: Math.Abs(y - edge))).Where(t => t.d < SnapDist).OrderBy(t => t.d).FirstOrDefault();
            if (hit.d < SnapDist && hit != default) { top = hit.y - off; gy = hit.y; break; }
        }
        Brush b = ThemeManager.Brush("B.Amber");
        if (gx is double x0) Guides.Children.Add(new Line { X1 = x0 * _scale, X2 = x0 * _scale, Y1 = 0, Y2 = Stage.ActualHeight, Stroke = b, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 } });
        if (gy is double y0) Guides.Children.Add(new Line { Y1 = y0 * _scale, Y2 = y0 * _scale, X1 = 0, X2 = Stage.ActualWidth, Stroke = b, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 4 } });
        return (left, top);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (RegionLayer.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { EndRegion(null); e.Handled = true; }
            return;
        }
        if (!Customizing || Keyboard.FocusedElement is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape) { SetCustomize(false); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)))) { Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { Apply(); e.Handled = true; return; }
        if (_selected is null) return;
        if (ctrl && e.Key == Key.D) { DuplicateElement(_selected); e.Handled = true; return; }
        if (e.Key == Key.Delete) { RemoveElement(_selected); e.Handled = true; return; }
        if (S.LayoutLocked || !_views.TryGetValue(_selected.Id, out var v)) return;
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        double l = Canvas.GetLeft(v), t = Canvas.GetTop(v);
        switch (e.Key)
        {
            case Key.Left: l -= step; break;
            case Key.Right: l += step; break;
            case Key.Up: t -= step; break;
            case Key.Down: t += step; break;
            default: return;
        }
        SetFromRect(_selected, l, t, v.ActualWidth, v.ActualHeight);
        Position(v);
        UpdateAdorner();
        _undoDebounce.Stop(); _undoDebounce.Start();
        e.Handled = true;
    }

    // ================================================================ element operations

    private void AddElement(ElementKind kind, string padId = "")
    {
        if (kind == ElementKind.Video && !Ui.RequirePro(ProFeature.Video)) return;
        var el = ElementCatalog.Create(kind, 0.5, 0.4);
        if (kind == ElementKind.NotePad) el.PadId = padId.Length > 0 ? padId : Pads.Current.Id;
        if (kind == ElementKind.Media) S.MediaVisible = true;
        P.ActivePage.Elements.Add(el);
        PushUndo();
        Refresh();
        Select(el);
    }

    private void DuplicateElement(OverlayElement el)
    {
        var c = el.Clone();
        c.X = Math.Min(1, el.X + 0.03);
        c.Y = Math.Min(1, el.Y + 0.03);
        c.Name = el.Name.Length > 0 ? el.Name + " copy" : "";
        P.ActivePage.Elements.Insert(P.ActivePage.Elements.IndexOf(el) + 1, c);
        PushUndo();
        Refresh();
        Select(c);
    }

    private void RemoveElement(OverlayElement el)
    {
        P.ActivePage.Elements.Remove(el);
        DataStore.Profile.Keybinds.RemoveAll(k => k.Action == "element." + el.Id);
        PushUndo();
        Select(null);
        Refresh();
        Status($"Removed {el.DisplayName} · Ctrl+Z brings it back");
    }

    private void ZOrder(OverlayElement el, int dir)
    {
        var list = P.ActivePage.Elements;
        int i = list.IndexOf(el), j = Math.Clamp(i + dir, 0, list.Count - 1);
        if (i == j) return;
        list.RemoveAt(i);
        list.Insert(j, el);
        PushUndo();
        Refresh();
    }

    private ContextMenu ElementMenu(OverlayElement el) => Ui.Menu(
        ("Edit…", () => { _menuTab = "EDIT"; BuildMenu(); }),
        (el.Visible ? "Hide" : "Show", () => { el.Visible = !el.Visible; PushUndo(); UpdateVisibility(); BuildMenu(); }),
        ("Duplicate", () => DuplicateElement(el)),
        ("Bring to front", () => ZOrder(el, +99)),
        ("Send to back", () => ZOrder(el, -99)),
        ("Reset look", () => { ElementEditor.ResetLook(el); PushUndo(); RebuildOne(el); BuildMenu(); }),
        ("Copy to another page…", () => CopyToPageMenu(el).IsOpen = true),
        (null, null),
        ("Remove", () => RemoveElement(el)));

    private ContextMenu CopyToPageMenu(OverlayElement el)
    {
        var m = new ContextMenu();
        foreach (var page in P.Pages.Where(p => p != P.ActivePage))
        {
            var pg = page;
            var mi = new MenuItem { Header = pg.Name };
            mi.Click += (_, _) => { pg.Elements.Add(el.Clone()); DataStore.MarkDirty(); ShowToast("COPIED", $"{el.DisplayName} → {pg.Name}"); };
            m.Items.Add(mi);
        }
        if (m.Items.Count == 0) m.Items.Add(new MenuItem { Header = "No other pages yet", IsEnabled = false });
        return m;
    }

    public void ToggleElement(string id)
    {
        foreach (var page in P.Pages)
        {
            var el = page.Elements.FirstOrDefault(e => e.Id == id);
            if (el is null) continue;
            el.Visible = !el.Visible;
            DataStore.MarkDirty();
            if (page != P.ActivePage) SwitchPage(page.Id);
            UpdateVisibility();
            ShowToast(el.DisplayName.ToUpperInvariant(), el.Visible ? "Shown" : "Hidden");
            return;
        }
    }

    public void ResetPositions()
    {
        var page = P.ActivePage;
        var fresh = P.Style == PresetStyle.Custom ? null : PresetCatalog.Create(P.Style).Pages.FirstOrDefault(p => p.Name == page.Name);
        int i = 0;
        foreach (var el in page.Elements)
        {
            var match = fresh?.Elements.FirstOrDefault(f => f.Kind == el.Kind && f.Title == el.Title);
            if (match is not null) { el.X = match.X; el.Y = match.Y; el.W = match.W; el.H = match.H; }
            else { el.X = (i % 3) * 0.5; el.Y = (i / 3) * 0.33; }
            el.HudClearedFor = ""; // re-checked against the game's HUD once, next time you're in game
            i++;
        }
        DataStore.MarkDirty();
        if (Customizing) PushUndo();
        Refresh();
        ShowToast("LAYOUT", $"Positions on \"{page.Name}\" reset.");
    }

    // ================================================================ pages

    public void SwitchPage(string id, bool toast = false)
    {
        if (P.Pages.All(p => p.Id != id) || P.ActivePageId == id) return;
        P.ActivePageId = id;
        DataStore.MarkDirty();
        _undo.Clear(); _redo.Clear(); _undo.Add(PageJson());
        Select(null);
        Refresh();
        if (toast) ShowToast("PAGE", P.ActivePage.Name);
    }

    public void StepPage(int dir)
    {
        // Locked pages (past the free limit) are skipped when cycling.
        var pages = P.Pages.Where((_, idx) => Edition.PageUsable(idx)).ToList();
        if (pages.Count == 0) return;
        int i = pages.IndexOf(P.ActivePage);
        SwitchPage(pages[(i + dir + pages.Count) % pages.Count].Id, toast: true);
    }

    private void AddPage()
    {
        if (P.Pages.Count >= 30) { ShowToast("PAGES", "Up to 30 pages per preset."); return; }
        string? name = Ui.Prompt(this, "New overlay page", DataStore.UniqueName("Custom Layout", P.Pages.Select(p => p.Name)));
        if (string.IsNullOrWhiteSpace(name)) return;
        var page = new OverlayPage { Name = name.Trim(), Elements = { ElementCatalog.Create(ElementKind.Strip, 0, 0) } };
        P.Pages.Add(page);
        DataStore.MarkDirty();
        SwitchPage(page.Id);
    }

    private void DuplicatePage(OverlayPage src)
    {
        var page = DataStore.DeepClone(src);
        page.Id = Ids.New();
        page.Name = DataStore.UniqueName(src.Name + " copy", P.Pages.Select(p => p.Name));
        foreach (var e in page.Elements) e.Id = Ids.New();
        P.Pages.Insert(P.Pages.IndexOf(src) + 1, page);
        DataStore.MarkDirty();
        SwitchPage(page.Id);
    }

    private void DeletePage(OverlayPage page)
    {
        if (P.Pages.Count <= 1) { ShowToast("PAGES", "A preset needs at least one page."); return; }
        if (S.ConfirmDeletes && !Ui.Confirm(this, "Delete page", $"Delete \"{page.Name}\" and its {page.Elements.Count} elements? REVERT can still bring it back until you press APPLY.")) return;
        int i = P.Pages.IndexOf(page);
        P.Pages.Remove(page);
        if (P.DefaultPageId == page.Id) P.DefaultPageId = P.Pages[0].Id;
        foreach (var l in P.Defcon.Levels.Where(l => l.AutoPageId == page.Id)) l.AutoPageId = "";
        DataStore.Profile.Keybinds.RemoveAll(k => k.Action == "page." + page.Id);
        P.ActivePageId = P.Pages[Math.Clamp(i - 1, 0, P.Pages.Count - 1)].Id;
        DataStore.MarkDirty();
        Refresh();
    }

    private void RenamePage(OverlayPage page)
    {
        string? name = Ui.Prompt(this, "Rename page", page.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        page.Name = name.Trim();
        DataStore.MarkDirty();
        BuildTopBar();
        BuildMenu();
    }

    private void MovePage(OverlayPage page, int dir)
    {
        int i = P.Pages.IndexOf(page), j = Math.Clamp(i + dir, 0, P.Pages.Count - 1);
        if (i == j) return;
        P.Pages.RemoveAt(i);
        P.Pages.Insert(j, page);
        DataStore.MarkDirty();
        BuildTopBar();
        BuildMenu();
    }

    private ContextMenu PageMenu(OverlayPage page) => Ui.Menu(
        ("Rename…", () => RenamePage(page)),
        ("Duplicate", () => DuplicatePage(page)),
        (P.DefaultPageId == page.Id ? "★ Default page" : "Make default page", () => { P.DefaultPageId = page.Id; DataStore.MarkDirty(); BuildMenu(); }),
        ("Move left", () => MovePage(page, -1)),
        ("Move right", () => MovePage(page, +1)),
        (null, null),
        ("Delete", () => DeletePage(page)));

    // ================================================================ top bar

    private Button Bar(string glyph, string text, string tip, Action a, bool on = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph.Length > 0) { var ic = Ui.Icon(glyph, 12, on ? "B.Accent" : "B.Dim"); ic.Margin = new Thickness(0, 0, text.Length > 0 ? 6 : 0, 0); content.Children.Add(ic); }
        if (text.Length > 0) { var t = Ui.Label(text, 10.5); if (on) t.SetResourceReference(TextBlock.ForegroundProperty, "B.Accent"); content.Children.Add(t); }
        var b = new Button { Content = content, Style = Ui.Style("Sf.IconButton"), ToolTip = tip, Padding = new Thickness(8, 0, 8, 0), Height = 30, Margin = new Thickness(1, 0, 1, 0) };
        b.Click += (_, _) => a();
        return b;
    }

    private UIElement Divider() { var d = new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0) }; d.SetResourceReference(Border.BackgroundProperty, "B.Line"); return d; }

    private void BuildTopBar()
    {
        if (!Customizing) return;
        // Wraps onto a second row on narrow (4:3 / CRT) screens instead of running off the edges.
        var row = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var logo = Ui.H2("SFHUD", 12);
        logo.Margin = new Thickness(4, 0, 10, 0);
        logo.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(logo);

        foreach (var page in P.Pages)
        {
            bool active = page == P.ActivePage;
            var tab = new RadioButton { Content = (P.DefaultPageId == page.Id ? "★ " : "") + page.Name.ToUpperInvariant(), Style = Ui.Style("Sf.Tab"), IsChecked = active, GroupName = "OvPages", ToolTip = "Click to switch · right-click for page options" };
            var pg = page;
            tab.Checked += (_, _) => SwitchPage(pg.Id);
            tab.MouseRightButtonUp += (_, e) => { PageMenu(pg).IsOpen = true; e.Handled = true; };
            row.Children.Add(tab);
        }
        row.Children.Add(Ui.ProFade(Bar("", "", "Add a page", AddPage), ProFeature.UnlimitedPages, !Edition.CanAddPage(P.Pages.Count)));
        row.Children.Add(Divider());
        var undo = Bar("", "", "Undo (Ctrl+Z)", Undo); undo.IsEnabled = _undo.Count > 1; row.Children.Add(undo);
        var redo = Bar("", "", "Redo (Ctrl+Y)", Redo); redo.IsEnabled = _redo.Count > 0; row.Children.Add(redo);
        row.Children.Add(Bar("", "GRID", "Snap to grid (hold Alt to move freely)", () => { S.SnapToGrid = !S.SnapToGrid; GridOverlay.Visibility = S.SnapToGrid ? Visibility.Visible : Visibility.Collapsed; if (S.SnapToGrid) DrawGrid(); DataStore.MarkDirty(); BuildTopBar(); }, S.SnapToGrid));
        row.Children.Add(Bar("", "GUIDES", "Alignment guides", () => { S.ShowGuides = !S.ShowGuides; DataStore.MarkDirty(); BuildTopBar(); }, S.ShowGuides));
        row.Children.Add(Bar(S.LayoutLocked ? "" : "", S.LayoutLocked ? "LOCKED" : "LOCK", "Lock the layout so nothing moves by accident", () => { S.LayoutLocked = !S.LayoutLocked; DataStore.MarkDirty(); BuildTopBar(); BuildAdorner(); }, S.LayoutLocked));
        row.Children.Add(Divider());
        row.Children.Add(Bar("", "MUSIC", "Show / hide the music player (Steam browser)", ToggleMusic, S.MediaVisible && P.ActivePage.Elements.Any(e => e.Kind == ElementKind.Media)));
        row.Children.Add(Bar("", "PADS", "Open note pads", () => CommandRequested?.Invoke("pad.window")));
        row.Children.Add(Bar("", "AI", "Open the Local AI menu", () => CommandRequested?.Invoke("ai.open")));
        row.Children.Add(Divider());
        row.Children.Add(Bar("", "REVERT", "Undo everything since you opened the menu (or since APPLY)", Revert));
        row.Children.Add(Bar("", "APPLY", "Save now and keep editing (Ctrl+S)", Apply));
        var done = new Button { Content = "DONE", Style = Ui.Style("Sf.Button.Small.Primary"), Margin = new Thickness(6, 0, 2, 0), ToolTip = "Save and hand control back to the game (Esc / Alt+~)" };
        done.Click += (_, _) => SetCustomize(false);
        row.Children.Add(done);

        var frame = new HudFrame { Content = row, Chamfer = 10, Padding = new Thickness(8, 5, 8, 5), ShowBrackets = false, ShowTab = true };
        frame.SetResourceReference(HudFrame.FillProperty, "B.BgElevated");
        frame.SetResourceReference(HudFrame.StrokeProperty, "B.Line.Strong");
        TopBar.Child = frame;
    }

    private void ToggleMusic()
    {
        SetMusicVisible(!(S.MediaVisible && P.ActivePage.Elements.Any(e => e.Kind == ElementKind.Media && e.Visible)));
        ShowToast("MUSIC PLAYER", S.MediaVisible ? $"Shown · controls {MediaService.Instance.ModeLabel.ToLowerInvariant()}" : "Hidden");
    }

    /// <summary>
    /// The one switch for the in-game music player (menu, keys, SFHud window). Showing it also un-hides the
    /// element itself, and adds one to this page when the preset has none anywhere.
    /// </summary>
    public void SetMusicVisible(bool on)
    {
        S.MediaVisible = on;
        if (on)
        {
            var all = P.Pages.SelectMany(p => p.Elements).Where(e => e.Kind == ElementKind.Media).ToList();
            foreach (var e in all) e.Visible = true;
            if (all.Count == 0) P.ActivePage.Elements.Add(ElementCatalog.Create(ElementKind.Media, 0, 1));
            _ = MediaService.Instance.StartAsync();
        }
        DataStore.MarkDirty();
        Refresh();
        if (Customizing) BuildTopBar();
    }

    // ================================================================ menu

    public void OpenMenu(string tab) { _menuTab = tab; if (!Customizing) SetCustomize(true, tab: tab); else BuildMenu(); }

    private void BuildMenu()
    {
        if (!Customizing) return;
        MenuHost.HorizontalAlignment = S.MenuOnLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        var tabs = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var t in new[] { "ELEMENTS", "EDIT", "PAGES", "CROSSHAIR", "DEFCON", "DISPLAY" })
        {
            var rb = new RadioButton { Content = Ui.TabContent(t, 11), Style = Ui.Style("Sf.Tab"), IsChecked = _menuTab == t, GroupName = "OvMenu", Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(0) };
            string tt = t;
            rb.Checked += (_, _) => { _menuTab = tt; _menuCollapsed = false; BuildMenu(); };
            rb.Click += (_, _) => { if (_menuCollapsed) { _menuCollapsed = false; BuildMenu(); } };
            tabs.Children.Add(rb);
        }
        var side = Ui.IconButton(S.MenuOnLeft ? "" : "", "Move this menu to the other side (turns off Auto side in DISPLAY)", () => { S.MenuOnLeft = !S.MenuOnLeft; S.MenuAutoSide = false; DataStore.MarkDirty(); BuildMenu(); });
        side.HorizontalAlignment = HorizontalAlignment.Right;
        // Collapse to just the tab strip so elements underneath can be seen and grabbed.
        var fold = Ui.IconButton(_menuCollapsed ? "" : "", _menuCollapsed ? "Expand this menu" : "Collapse this menu (see the elements under it)", () => { _menuCollapsed = !_menuCollapsed; BuildMenu(); });


        var body = _menuTab switch
        {
            "EDIT" => EditTab(),
            "PAGES" => PagesTab(),
            "CROSSHAIR" => CrosshairTab(),
            "DEFCON" => DefconTab(),
            "DISPLAY" => DisplayTab(),
            _ => ElementsTab(),
        };
        var dock = new DockPanel();
        var head = new DockPanel();
        var headButtons = Ui.HStack(fold, side);
        headButtons.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(headButtons, Dock.Right);
        head.Children.Add(headButtons);
        head.Children.Add(tabs);
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        if (!_menuCollapsed) dock.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) });
        MenuHost.VerticalAlignment = _menuCollapsed ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        var frame = new HudFrame { Content = dock, Chamfer = 12, Padding = new Thickness(14, 10, 10, 12), Glow = false };
        frame.SetResourceReference(HudFrame.FillProperty, "B.Bg");
        frame.SetResourceReference(HudFrame.StrokeProperty, "B.Line.Strong");
        MenuHost.Child = frame;
    }

    private UIElement ElementsTab()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.H2($"ON \"{P.ActivePage.Name.ToUpperInvariant()}\""));
        sp.Children.Add(Ui.Hint("Click to edit. The eye hides an element without deleting it."));
        foreach (var el in Enumerable.Reverse(P.ActivePage.Elements))
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var e = el;
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Ui.IconButton(e.Visible ? "" : "", e.Visible ? "Hide" : "Show", () => { e.Visible = !e.Visible; PushUndo(); UpdateVisibility(); BuildMenu(); }));
            actions.Children.Add(Ui.IconButton("", "Bring forward", () => ZOrder(e, +1)));
            actions.Children.Add(Ui.IconButton("", "Send backward", () => ZOrder(e, -1)));
            actions.Children.Add(Ui.IconButton("", "Remove (Ctrl+Z undoes)", () => RemoveElement(e)));
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var ic = Ui.Icon(ElementCatalog.Glyph(e.Kind), 13, ReferenceEquals(e, _selected) ? "B.Amber" : "B.Accent");
            ic.Margin = new Thickness(4, 0, 8, 0);
            row.Children.Add(ic);
            var name = Ui.Text(e.DisplayName + (e.Kind == ElementKind.NotePad && P.Pad(e.PadId) is { } pd ? $" · {pd.Title}" : e.Kind == ElementKind.InfoCard && e.Title.Length > 0 ? $" · {e.Title}" : ""), 12.5, e.Visible ? "B.Text" : "B.Muted", wrap: false);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            row.MouseLeftButtonUp += (_, _) => Select(e);
            sp.Children.Add(row);
        }
        sp.Children.Add(Ui.Spacer(10));
        sp.Children.Add(Ui.H2("ADD ELEMENT"));
        var add = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var k in ElementCatalog.Addable)
        {
            var kind = k;
            var b = Ui.Button(ElementCatalog.Name(k).ToUpperInvariant(), () =>
            {
                if (kind == ElementKind.NotePad && P.Pads.Count > 1)
                {
                    var m = new ContextMenu();
                    foreach (var pad in P.Pads) { var mi = new MenuItem { Header = pad.Title }; var pp = pad; mi.Click += (_, _) => AddElement(kind, pp.Id); m.Items.Add(mi); }
                    m.IsOpen = true;
                }
                else AddElement(kind);
            });
            if (kind == ElementKind.Video) Ui.ProFade(b, ProFeature.Video);
            add.Children.Add(b);
        }
        sp.Children.Add(add);
        return sp;
    }

    private UIElement EditTab()
    {
        if (_selected is null)
            return Ui.Stack(Ui.H2("NOTHING SELECTED"), Ui.Hint("Click an element on screen (or in the ELEMENTS tab) to edit its text, size, colours, font, border and which DEFCON levels show it."));
        var el = _selected;
        return ElementEditor.Build(el, () => Changed(el), delete: () => RemoveElement(el), duplicate: () => DuplicateElement(el),
            reset: () => { ElementEditor.ResetLook(el); PushUndo(); RebuildOne(el); BuildMenu(); });
    }

    private UIElement PagesTab()
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.H2("PAGES"));
        sp.Children.Add(Ui.Hint("Each page has its own elements, positions and colours. Right-click a page tab at the top for the same options."));
        foreach (var page in P.Pages)
        {
            var pg = page;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Ui.IconButton(P.DefaultPageId == pg.Id ? "" : "", "Default page (shown at start)", () => { P.DefaultPageId = pg.Id; DataStore.MarkDirty(); BuildMenu(); BuildTopBar(); }));
            actions.Children.Add(Ui.IconButton("", "Rename", () => RenamePage(pg)));
            actions.Children.Add(Ui.ProFade(Ui.IconButton("", "Duplicate", () => DuplicatePage(pg)), ProFeature.UnlimitedPages, !Edition.CanAddPage(P.Pages.Count)));
            actions.Children.Add(Ui.IconButton("", "Move up", () => MovePage(pg, -1)));
            actions.Children.Add(Ui.IconButton("", "Move down", () => MovePage(pg, +1)));
            actions.Children.Add(Ui.IconButton("", "Delete", () => DeletePage(pg)));
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var b = new Button { Content = pg.Name, Style = Ui.Style(pg == P.ActivePage ? "Sf.Button.Small.Primary" : "Sf.Button.Small"), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = App.Gesture("page." + pg.Id) is { Length: > 0 } g ? "Key: " + g : "Set a key in the SFHud window › Overlay › Keybinds" };
            b.Click += (_, _) => SwitchPage(pg.Id);
            row.Children.Add(b);
            sp.Children.Add(row);
        }
        sp.Children.Add(Ui.ProFade(Ui.Button("+ NEW PAGE", AddPage), ProFeature.UnlimitedPages, !Edition.CanAddPage(P.Pages.Count)));

        sp.Children.Add(Ui.Spacer(10));
        sp.Children.Add(Ui.H2("SAVED LAYOUTS"));
        sp.Children.Add(Ui.Hint("Save the current page's layout and load it back onto any page later."));
        sp.Children.Add(Ui.Button("SAVE CURRENT PAGE AS LAYOUT…", () =>
        {
            string? n = Ui.Prompt(this, "Save layout", P.ActivePage.Name + " " + DateTime.Now.ToString("MMM d"));
            if (string.IsNullOrWhiteSpace(n)) return;
            P.Layouts.Add(new LayoutSnapshot { Name = n.Trim(), Page = DataStore.DeepClone(P.ActivePage) });
            DataStore.MarkDirty();
            BuildMenu();
        }));
        foreach (var snap in P.Layouts.OrderByDescending(l => l.Saved))
        {
            var s = snap;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(Ui.Button("LOAD", () =>
            {
                var els = DataStore.DeepClone(s.Page.Elements);
                foreach (var e in els) e.Id = Ids.New();
                P.ActivePage.Elements = els;
                PushUndo();
                Refresh();
                ShowToast("LAYOUT", $"Loaded \"{s.Name}\" onto this page. Ctrl+Z undoes.");
            }));
            actions.Children.Add(Ui.IconButton("", "Delete saved layout", () => { P.Layouts.Remove(s); DataStore.MarkDirty(); BuildMenu(); }));
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            row.Children.Add(Ui.Text($"{s.Name}  ·  {s.Page.Elements.Count} elements", 12));
            sp.Children.Add(row);
        }

        sp.Children.Add(Ui.Spacer(10));
        sp.Children.Add(Ui.H2("RESET"));
        sp.Children.Add(Ui.Button("RESET POSITIONS ON THIS PAGE", ResetPositions));
        if (P.Style != PresetStyle.Custom)
            sp.Children.Add(Ui.Button("RESET PAGE TO PRESET DEFAULT", () =>
            {
                var fresh = PresetCatalog.Create(P.Style).Pages.FirstOrDefault(p => p.Name == P.ActivePage.Name);
                if (fresh is null) { ShowToast("RESET", "This page isn't part of the built-in preset."); return; }
                foreach (var e in fresh.Elements.Where(e => e.Kind == ElementKind.NotePad))
                    e.PadId = P.Pads.FirstOrDefault(p => p.Title == PresetCatalog.Create(P.Style).Pads.FirstOrDefault(x => x.Id == e.PadId)?.Title)?.Id ?? Pads.Current.Id;
                P.ActivePage.Elements = fresh.Elements;
                PushUndo();
                Refresh();
            }, "Sf.Button.Small.Danger"));
        return sp;
    }

    private UIElement CrosshairTab()
    {
        var sp = new StackPanel();
        void C() { DataStore.MarkDirty(); ApplyCrosshair(); }
        sp.Children.Add(Ui.Switch("Crosshair on", S.CrosshairEnabled, v => { S.CrosshairEnabled = v; C(); }, "Drawn by SFHud on its own window. Game files and memory are never touched."));
        sp.Children.Add(Ui.Label("STYLE"));
        var styles = new WrapPanel { Margin = new Thickness(0, 4, 0, 8) };
        foreach (var st in Enum.GetValues<CrosshairStyle>())
        {
            var chip = new ToggleButton { Content = st.ToString().ToUpperInvariant(), Style = Ui.Style("Sf.Chip"), IsChecked = S.CrosshairStyle == st };
            var s = st;
            chip.Click += (_, _) => { S.CrosshairStyle = s; S.CrosshairEnabled = true; C(); BuildMenu(); };
            styles.Children.Add(chip);
        }
        sp.Children.Add(styles);
        sp.Children.Add(Ui.Label("COLOUR"));
        sp.Children.Add(Ui.ColorPicker(S.CrosshairColor, v => { if (v.Length > 0) { S.CrosshairColor = v; C(); } }, allowDefault: false));
        sp.Children.Add(Ui.Slider("Size", 6, 64, S.CrosshairSize, v => $"{v:0} px", v => { S.CrosshairSize = Math.Round(v); C(); }, step: 1));
        sp.Children.Add(Ui.Slider("Thickness", 1, 6, S.CrosshairThickness, v => $"{v:0.#} px", v => { S.CrosshairThickness = Math.Round(v * 2) / 2; C(); }, step: 0.5));
        sp.Children.Add(Ui.Slider("Gap (left = auto)", -1, 20, S.CrosshairGap, v => v < 0 ? "AUTO" : $"{v:0} px", v => { S.CrosshairGap = Math.Round(v); C(); }, step: 1));
        sp.Children.Add(Ui.Slider("Opacity", 0.1, 1, S.CrosshairOpacity, v => $"{v:P0}", v => { S.CrosshairOpacity = Math.Round(v, 2); C(); }));
        sp.Children.Add(Ui.Switch("Center dot", S.CrosshairCenterDot, v => { S.CrosshairCenterDot = v; C(); }));
        sp.Children.Add(Ui.Switch("Outline", S.CrosshairOutline, v => { S.CrosshairOutline = v; C(); }));
        sp.Children.Add(Ui.Slider("Outline thickness", 0.5, 4, S.CrosshairOutlineThickness, v => $"{v:0.#} px", v => { S.CrosshairOutlineThickness = Math.Round(v * 2) / 2; C(); }, step: 0.5));
        sp.Children.Add(Ui.Label("OUTLINE COLOUR"));
        sp.Children.Add(Ui.ColorPicker(S.CrosshairOutlineColor, v => { if (v.Length > 0) { S.CrosshairOutlineColor = v; C(); } }, allowDefault: false));

        sp.Children.Add(Ui.Spacer(8));
        sp.Children.Add(Ui.H2("PRESETS"));
        var presets = DataStore.Profile.CrosshairPresets;
        foreach (var pr in presets)
        {
            var p = pr;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var del = Ui.IconButton("", "Delete preset", () => { presets.Remove(p); DataStore.MarkDirty(); BuildMenu(); });
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            row.Children.Add(Ui.Button(p.Name.ToUpperInvariant(), () => { CrosshairPresets.Apply(p, S); S.CrosshairEnabled = true; C(); BuildMenu(); }));
            sp.Children.Add(row);
        }
        sp.Children.Add(Ui.Button("SAVE CURRENT AS PRESET…", () =>
        {
            string? n = Ui.Prompt(this, "Crosshair preset", $"{S.CrosshairStyle} {S.CrosshairColor}");
            if (string.IsNullOrWhiteSpace(n)) return;
            presets.Add(CrosshairPresets.From(S, n.Trim()));
            DataStore.MarkDirty();
            BuildMenu();
        }, "Sf.Button.Small.Primary"));
        return sp;
    }

    private UIElement DefconTab()
    {
        var sp = new StackPanel();
        var svc = DefconService.Instance;
        if (!DefconService.Enabled)
        {
            sp.Children.Add(Ui.Hint("DEFCON is off for this preset."));
            sp.Children.Add(Ui.Button("TURN ON", () => { P.DefconEnabled = true; DataStore.NotifyContext(); BuildMenu(); }, "Sf.Button.Small.Primary"));
            return sp;
        }
        sp.Children.Add(Ui.H2("SET LEVEL"));
        var lv = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var l in P.Defcon.Levels)
        {
            var b = new Button { Content = l.Short + " " + l.Name.ToUpperInvariant(), Style = Ui.Style(l.Id == P.Defcon.ActiveLevelId ? "Sf.Button.Small.Primary" : "Sf.Button.Small"), Margin = new Thickness(0, 0, 5, 5), Foreground = l.Id == P.Defcon.ActiveLevelId ? null : ThemeManager.BrushOr(l.Color, "B.Accent"), ToolTip = l.Description };
            var id = l.Id;
            b.Click += (_, _) => { svc.SetById(id); BuildMenu(); };
            lv.Children.Add(b);
        }
        sp.Children.Add(lv);
        sp.Children.Add(Ui.Button("CLEAR", () => { svc.SetLevel(null, "cleared"); BuildMenu(); }));
        if (svc.Pending is { } p)
        {
            sp.Children.Add(Ui.Spacer(6));
            sp.Children.Add(Ui.H2("SUGGESTION"));
            sp.Children.Add(Ui.Text($"{p.Level.Label} · {p.Source} · {p.ConfidenceText} sure", 12.5, "B.Amber"));
            if (p.Evidence.Length > 0) sp.Children.Add(Ui.Hint("“" + p.Evidence + "”"));
            sp.Children.Add(Ui.HStack(Ui.Button("ACCEPT", () => { svc.Accept(); BuildMenu(); }, "Sf.Button.Small.Primary"), Ui.Button("DISMISS", () => { svc.Dismiss(); BuildMenu(); })));
        }
        sp.Children.Add(Ui.Spacer(8));
        sp.Children.Add(Ui.H2("LAYOUT PER LEVEL"));
        if (!Edition.Has(ProFeature.LevelLayouts)) sp.Children.Add(Ui.ProPanel(ProFeature.LevelLayouts, "Each level gets its own page and the overlay switches by itself."));
        else sp.Children.Add(Ui.Hint("The page each level switches to. Click one to pick a page, or make a copy of this page for it."));
        foreach (var lvl in Edition.Has(ProFeature.LevelLayouts) ? P.Defcon.Levels : new List<DefconLevel>())
        {
            var l = lvl;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var pick = new Button { Content = DefconLayouts.PageName(l), Style = Ui.Style(l.AutoPageId == P.ActivePageId && l.AutoPageId.Length > 0 ? "Sf.Button.Small.Primary" : "Sf.Button.Small"), MinWidth = 130, ToolTip = "Pick the page for " + l.Label };
            pick.Click += (_, _) =>
            {
                var m = new ContextMenu();
                void Item(string header, Action a, bool check = false) { var mi = new MenuItem { Header = (check ? "✓  " : "     ") + header }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
                Item("Stay on current page", () => { DefconLayouts.Assign(l, ""); BuildMenu(); }, l.AutoPageId.Length == 0);
                foreach (var pg in P.Pages) { var id = pg.Id; Item(pg.Name, () => { DefconLayouts.Assign(l, id); BuildMenu(); }, l.AutoPageId == id); }
                m.Items.Add(new Separator());
                Item($"New layout from \"{P.ActivePage.Name}\"", () =>
                {
                    if (DefconLayouts.CreateFor(l) is { } np) { SwitchPage(np.Id); ShowToast("LAYOUT", $"Editing \"{np.Name}\" · shown at {l.Label}"); }
                    else ShowToast("PAGES", "Up to 30 pages per preset.");
                    BuildMenu(); BuildTopBar();
                });
                if (DefconLayouts.PageFor(l) is { } cur) Item($"Edit \"{cur.Name}\" now", () => { SwitchPage(cur.Id); BuildMenu(); BuildTopBar(); });
                m.PlacementTarget = pick;
                m.IsOpen = true;
            };
            DockPanel.SetDock(pick, Dock.Right);
            row.Children.Add(pick);
            var name = Ui.Text(l.Label, 12, "B.Text", wrap: false);
            name.Foreground = ThemeManager.BrushOr(l.Color, "B.Accent");
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            sp.Children.Add(row);
        }
        sp.Children.Add(Ui.Switch("Back to the default page at levels without one", P.Defcon.ReturnToDefaultPage, v => { P.Defcon.ReturnToDefaultPage = v; DataStore.MarkDirty(); }));

        sp.Children.Add(Ui.Spacer(8));
        sp.Children.Add(Ui.H2("DETECTION"));
        var det = P.Defcon.Detection;
        sp.Children.Add(Ui.Hint("Verified screen readings switch the level by themselves. With AI auto-switch on, so do announcements the local AI reads. Ctrl+Alt+End goes back to the default DEFCON."));
        sp.Children.Add(Ui.Switch("Detection on", det.Enabled, v => { det.Enabled = v; DataStore.MarkDirty(); }));
        sp.Children.Add(Ui.ProLock(Ui.Switch("Read the screen (OCR)", det.Ocr, v => { det.Ocr = v; DataStore.MarkDirty(); BuildMenu(); }, "Windows' offline OCR reads a small box of the game where your server shows its alert level."), ProFeature.DefconAutoDetect));
        sp.Children.Add(Ui.ProLock(Ui.HStack(Ui.Button("PICK SCREEN REGION", () => PickRegion(r => { det.OcrX = r.X; det.OcrY = r.Y; det.OcrW = r.Width; det.OcrH = r.Height; det.Ocr = true; DataStore.MarkDirty(); BuildMenu(); })),
            Ui.Button("SCAN NOW", async () => { Status("Scanning…"); string res = await svc.ScanNowAsync(); Status(res.Replace('\n', ' ')); BuildMenu(); })), ProFeature.DefconAutoDetect));
        if (svc.LastOcrText.Length > 0) sp.Children.Add(Ui.Hint("Screen read: " + svc.LastOcrText));
        sp.Children.Add(Ui.Switch("Watch console.log announcements", det.ConsoleLog, v => { det.ConsoleLog = v; DataStore.MarkDirty(); }));
        sp.Children.Add(Ui.ProLock(Ui.Switch("Use the local AI", det.AiAssist, v => { det.AiAssist = v; DataStore.MarkDirty(); }), ProFeature.LocalAi));
        sp.Children.Add(Ui.ProLock(Ui.Switch("AI switches DEFCON automatically", det.AiAutoApply, v => { det.AiAutoApply = v; DataStore.MarkDirty(); }, "Undo with Ctrl+Alt+End (Back to default DEFCON)."), ProFeature.LocalAi));
        sp.Children.Add(Ui.Button("BACK TO DEFAULT DEFCON", () => { svc.ResetToDefault(); BuildMenu(); }));
        sp.Children.Add(Ui.Switch("Auto-apply confident rule matches", det.AutoApply == AutoApplyMode.HighConfidence, v => { det.AutoApply = v ? AutoApplyMode.HighConfidence : AutoApplyMode.Never; DataStore.MarkDirty(); }, $"Only rule-based matches at {det.Threshold:P0}+ confidence."));
        sp.Children.Add(Ui.Hint("Rename levels, add or remove them, and set per-level pages, pads and sounds in the SFHud window › DEFCON."));
        return sp;
    }

    private UIElement DisplayTab()
    {
        var sp = new StackPanel();
        void C() { DataStore.MarkDirty(); UpdateVisibility(); }
        sp.Children.Add(Ui.Slider("Overlay opacity", 0.2, 1, S.OverlayOpacity, v => $"{v:P0}", v => { S.OverlayOpacity = Math.Round(v, 2); ApplyOpacity(); DataStore.MarkDirty(); }));
        sp.Children.Add(Ui.Slider("Overlay scale", 0.6, 3, S.UiScale, v => $"{v:0.00}×", v => { S.UiScale = Math.Round(v, 2); Relayout(); DataStore.MarkDirty(); }));
        sp.Children.Add(Ui.Switch("Scale with game resolution", S.ScaleWithResolution, v => { S.ScaleWithResolution = v; Relayout(); DataStore.MarkDirty(); }, "1080p = 1×. Layouts look the same at 1440p and 4K, and fit 4:3 and 5:4 screens."));
        sp.Children.Add(Ui.Slider("Smallest text on low resolutions", 0.5, 1.2, S.MinReadableScale, v => $"{v:0.00}×", v => { S.MinReadableScale = Math.Round(v, 2); Relayout(); DataStore.MarkDirty(); }, "Floor for resolution scaling. Raise it if notes are hard to read at 800×600 / 1024×768 (CRT, 4:3)."));
        sp.Children.Add(Ui.Switch("Scale this menu and toasts with the screen", S.ScaleOverlayMenu, v => { S.ScaleOverlayMenu = v; ApplyChromeScale(); DataStore.MarkDirty(); }, "Bigger on 1440p / 4K, fitted on 4:3 screens."));
        sp.Children.Add(Ui.Switch("Keep clear of the game's HUD", S.AvoidGameHud, v => { S.AvoidGameHud = v; Relayout(); DataStore.MarkDirty(); }, "Amber boxes show the game's HUD. Dropping an element on one moves it just clear (hold Alt to place it anyway). Elements stay exactly where you put them."));
        sp.Children.Add(Ui.Switch("Menu picks the emptier side", S.MenuAutoSide, v => { S.MenuAutoSide = v; DataStore.MarkDirty(); ArrangeChrome(); }, "This menu moves to whichever side covers fewer of your elements. The arrow at the top flips it by hand."));
        sp.Children.Add(Ui.Switch("Info cards", S.PinsVisible, v => { S.PinsVisible = v; C(); }));
        sp.Children.Add(Ui.Switch("Timers", S.TimersVisible, v => { S.TimersVisible = v; C(); }));
        sp.Children.Add(Ui.Switch("Music player", S.MediaVisible, v => SetMusicVisible(v)));
        sp.Children.Add(Ui.Switch("Distraction-free (only crosshair and toasts)", S.DistractionFree, v => { S.DistractionFree = v; C(); }));
        sp.Children.Add(Ui.Switch("Toasts", S.Toasts, v => { S.Toasts = v; DataStore.MarkDirty(); }));
        sp.Children.Add(Ui.Slider("Toast duration", 2, 15, S.ToastSeconds, v => $"{v:0.#} s", v => { S.ToastSeconds = Math.Round(v * 2) / 2; DataStore.MarkDirty(); }, step: 0.5));
        sp.Children.Add(Ui.Slider("Grid size", 4, 32, S.GridSize, v => $"{v:0}", v => { S.GridSize = Math.Round(v); DrawGrid(); DataStore.MarkDirty(); }, step: 2));
        sp.Children.Add(Ui.Switch("Hide SFHud from screenshots / OBS", S.ExcludeFromCapture, v => { S.ExcludeFromCapture = v; ApplyCaptureAffinity(); DataStore.MarkDirty(); }, "Windows 10 2004+. Also keeps the overlay out of SFHud's own screen reading."));
        sp.Children.Add(Ui.Spacer(6));
        sp.Children.Add(Ui.Button("OPEN SFHUD WINDOW", () => { SetCustomize(false, focusGame: false); CommandRequested?.Invoke("dashboard.open"); }));
        return sp;
    }

    // ================================================================ crosshair

    public void ApplyCrosshair()
    {
        CrosshairLayer.Visibility = S.CrosshairEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (S.CrosshairEnabled) CrosshairGeometry.Apply(CrosshairPath, CrosshairShadow, CrosshairLayer, S);
    }

    // ================================================================ OCR region picker

    public void PickRegion(Action<Rect> done)
    {
        if (!Engaged) Engage();
        _regionDone = done;
        bool wasCustomizing = Customizing;
        MenuHost.Visibility = TopBar.Visibility = Visibility.Collapsed;
        SetClickThrough(false);
        IsHitTestVisible = true;
        Focusable = true;
        RegionLayer.Visibility = Visibility.Visible;
        RegionRect.Visibility = Visibility.Collapsed;
        ShowAndActivate();
        Focus();
        _regionWasCustomizing = wasCustomizing;
    }

    private bool _regionWasCustomizing;

    private void Region_Down(object sender, MouseButtonEventArgs e)
    {
        _regionStart = e.GetPosition(RegionLayer);
        RegionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(RegionRect, _regionStart.X); Canvas.SetTop(RegionRect, _regionStart.Y);
        RegionRect.Width = RegionRect.Height = 0;
        RegionLayer.CaptureMouse();
    }

    private void Region_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || RegionRect.Visibility != Visibility.Visible) return;
        var p = e.GetPosition(RegionLayer);
        Canvas.SetLeft(RegionRect, Math.Min(p.X, _regionStart.X)); Canvas.SetTop(RegionRect, Math.Min(p.Y, _regionStart.Y));
        RegionRect.Width = Math.Abs(p.X - _regionStart.X); RegionRect.Height = Math.Abs(p.Y - _regionStart.Y);
    }

    private void Region_Up(object sender, MouseButtonEventArgs e)
    {
        RegionLayer.ReleaseMouseCapture();
        double w = RegionLayer.ActualWidth, h = RegionLayer.ActualHeight;
        if (RegionRect.Width < 6 || RegionRect.Height < 6 || w <= 0) return;
        EndRegion(new Rect(Canvas.GetLeft(RegionRect) / w, Canvas.GetTop(RegionRect) / h, RegionRect.Width / w, RegionRect.Height / h));
    }

    private void EndRegion(Rect? r)
    {
        RegionLayer.Visibility = Visibility.Collapsed;
        var cb = _regionDone;
        _regionDone = null;
        if (_regionWasCustomizing) { MenuHost.Visibility = TopBar.Visibility = Visibility.Visible; }
        else { SetClickThrough(true); IsHitTestVisible = false; Focusable = false; GameLocator.FocusTarget(_target); }
        if (r is { } rect) { cb?.Invoke(rect); ShowToast("DEFCON SCREEN REGION", "Saved. SFHud reads it every few seconds while the game is in front."); }
    }

    // ================================================================ toasts

    public void ShowToast(string title, string text, bool urgent = false, string colorHex = "")
    {
        if (!Engaged || !S.Toasts) return;
        Toasts.Margin = new Thickness(0, Math.Max(80, ActualHeight * 0.16), 0, 0);
        var accent = colorHex.Length > 0 ? ThemeManager.BrushOr(colorHex, "B.Accent") : ThemeManager.Brush(urgent ? "B.Amber" : "B.Accent");
        var title_ = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = accent };
        title_.SetResourceReference(TextBlock.FontFamilyProperty, "F.Display");
        var body = new TextBlock { Text = text, Margin = new Thickness(0, 3, 0, 0), FontSize = 13, TextWrapping = TextWrapping.Wrap };
        body.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        var frame = new HudFrame
        {
            Chamfer = 8, Padding = new Thickness(14, 8, 14, 9), Margin = new Thickness(0, 0, 0, 8), ShowBrackets = false, ShowTab = true,
            Stroke = accent, TabBrush = accent, Glow = urgent, Content = new StackPanel { Children = { title_, body } },
        };
        frame.SetResourceReference(HudFrame.FillProperty, "B.Card");
        Toasts.Children.Insert(0, frame);
        while (Toasts.Children.Count > 3) Toasts.Children.RemoveAt(Toasts.Children.Count - 1);

        var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(S.ToastSeconds, 1, 30) * (urgent ? 1.4 : 1)) };
        life.Tick += (_, _) =>
        {
            life.Stop();
            if (App.ReducedMotion) { Toasts.Children.Remove(frame); return; }
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (_, _) => Toasts.Children.Remove(frame);
            frame.BeginAnimation(OpacityProperty, fade);
        };
        life.Start();
    }

    /// <summary>Brief flash on DEFCON elements when the level changes.</summary>
    public void FlashDefcon()
    {
        if (App.ReducedMotion || !IsVisible) return;
        foreach (var v in _views.Values.Where(v => v.El.Kind is ElementKind.Defcon or ElementKind.Strip))
            v.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, Math.Clamp(v.El.Opacity, 0.1, 1), TimeSpan.FromMilliseconds(220)) { RepeatBehavior = new RepeatBehavior(3) });
    }

    // ================================================================ tracking

    /// <summary>Set by the main window: true for SFHud's main window and dialogs it owns (the overlay hides behind those).</summary>
    public Func<IntPtr, bool>? IsMainWindow { get; set; }

    /// <summary>The customize menu and region picker need the overlay up even with no game (shown over the desktop as a preview).</summary>
    private bool ForcedUp => Customizing || RegionLayer.Visibility == Visibility.Visible || App.ScreenshotMode;

    /// <summary>
    /// When the overlay may show. Only ever over the selected game or a whitelisted app, and only while
    /// it is the foreground window. SFHud's own pop-ups opened over the game (quick note, timer, AI, pads)
    /// keep it up; the main SFHud window hides it. Everything else (desktop, browser, Discord…) hides it.
    /// Same approach as external overlays that anti-cheats allow: a separate click-through topmost window
    /// positioned with Win32 window events. Nothing is injected into the game.
    /// </summary>
    private void Track()
    {
        if (!Engaged) return;
        IntPtr game = App.ScreenshotMode ? IntPtr.Zero : GameLocator.Find(_target);
        EnsureHooks(game);
        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint fgPid);
        bool ours = fgPid == (uint)Environment.ProcessId;
        if (!ours) _gameWasLastForeground = fg == game && game != IntPtr.Zero;

        if (game == IntPtr.Zero || IsIconic(game))
        {
            if (!ForcedUp)
            {
                HideOverlay();
                Report(false, false);
                SetState(game == IntPtr.Zero ? "WAITING FOR GAME" : "HIDDEN · GAME MINIMIZED");
                return;
            }
            // Preview: no game window, show over the work area of the monitor SFHud is on.
            var wa = SystemParameters.WorkArea;
            if (!IsVisible || _lastRect.Width != 0 || Left != wa.Left || Top != wa.Top || Width != wa.Width)
            {
                _lastRect = default;
                Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;
                Show();
            }
            SetState(Customizing ? "CUSTOMIZING · PREVIEW" : "PREVIEW");
            Report(false, false);
            return;
        }

        bool mainInFront = ours && fg != _hwnd && (IsMainWindow?.Invoke(fg) ?? false);
        bool show = ForcedUp
            || fg == game
            || (ours && !mainInFront && _gameWasLastForeground);
        if (!show)
        {
            HideOverlay();
            Report(false, !ours && fg != game && fg != IntPtr.Zero);
            SetState(mainInFront ? "HIDDEN · SFHUD IN FRONT" : "HIDDEN · GAME UNFOCUSED");
            return;
        }

        GetClientRect(game, out RECT client);
        var origin = new POINT();
        ClientToScreen(game, ref origin);
        var rect = new RECT { Left = origin.X, Top = origin.Y, Right = origin.X + client.Right, Bottom = origin.Y + client.Bottom };

        if (!IsVisible) Show();
        if (!rect.Equals(_lastRect) && rect.Width > 0 && rect.Height > 0)
        {
            // Physical pixels (per-monitor DPI aware), so this tracks resizes, moves and DPI changes exactly.
            SetWindowPos(_hwnd, HWND_TOPMOST, rect.Left, rect.Top, rect.Width, rect.Height, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            _lastRect = rect;
        }
        SetState(Customizing ? "CUSTOMIZING" : "ATTACHED");
        Report(true, false);
    }

    // ---------------------------------------------------------------- window-event hooks
    // Out-of-context WinEvent hooks (no DLL goes into the game): the game's moves, resizes and
    // minimize/restore reach SFHud straight away instead of waiting for the next 250 ms poll.

    private bool _gameWasLastForeground;
    private readonly WinEventDelegate _winEventProc;
    private IntPtr _locHook, _minHook, _hookedGame;
    private bool _trackQueued;

    private void EnsureHooks(IntPtr game)
    {
        if (game == _hookedGame) return;
        Unhook();
        _hookedGame = game;
        if (game == IntPtr.Zero) return;
        GetWindowThreadProcessId(game, out uint pid);
        if (pid == 0) return;
        _locHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);
        _minHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, _winEventProc, pid, 0, WINEVENT_OUTOFCONTEXT);
    }

    private void Unhook()
    {
        if (_locHook != IntPtr.Zero) UnhookWinEvent(_locHook);
        if (_minHook != IntPtr.Zero) UnhookWinEvent(_minHook);
        _locHook = _minHook = _hookedGame = IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != _hookedGame || idObject != OBJID_WINDOW || _trackQueued) return;
        _trackQueued = true;
        Dispatcher.BeginInvoke(() => { _trackQueued = false; Track(); }, DispatcherPriority.Render);
    }

    private void HideOverlay()
    {
        if (IsVisible) Hide();
        _lastRect = default;
    }

    private void SetState(string state)
    {
        if (State == state) return;
        State = state;
        OverlayStateChanged?.Invoke(state);
    }
}

public static class CrosshairPresets
{
    public static CrosshairPreset From(AppSettings s, string name) => new()
    {
        Name = name, Style = s.CrosshairStyle, Color = s.CrosshairColor, Size = s.CrosshairSize, Thickness = s.CrosshairThickness, Gap = s.CrosshairGap,
        Outline = s.CrosshairOutline, OutlineThickness = s.CrosshairOutlineThickness, OutlineColor = s.CrosshairOutlineColor, Opacity = s.CrosshairOpacity, CenterDot = s.CrosshairCenterDot,
    };

    public static void Apply(CrosshairPreset p, AppSettings s)
    {
        s.CrosshairStyle = p.Style; s.CrosshairColor = p.Color; s.CrosshairSize = p.Size; s.CrosshairThickness = p.Thickness; s.CrosshairGap = p.Gap;
        s.CrosshairOutline = p.Outline; s.CrosshairOutlineThickness = p.OutlineThickness; s.CrosshairOutlineColor = p.OutlineColor; s.CrosshairOpacity = p.Opacity; s.CrosshairCenterDot = p.CenterDot;
        DataStore.MarkDirty();
    }
}
