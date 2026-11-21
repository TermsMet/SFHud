using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SFHud.Interop;
using SFHud.Services;

namespace SFHud.Controls;

/// <summary>What an element needs from its host (overlay, companion window, preview).</summary>
public sealed class ElementContext
{
    /// <summary>Buttons, links and checkboxes respond (overlay customize mode, companion window).</summary>
    public bool Interactive { get; init; }
    public Func<string> TargetName { get; init; } = () => "";
    public Func<string> SessionText { get; init; } = () => "--:--:--";
    public Func<bool> GameAttached { get; init; } = () => false;
    /// <summary>Only the overlay plays videos (a video can only be on screen in one place).</summary>
    public bool HostsVideo { get; init; }
}

/// <summary>
/// Draws one <see cref="OverlayElement"/> with its own colours, font, border and size. Shared by the
/// overlay, the companion window and the editor previews, so they always match.
/// </summary>
public sealed class ElementView : Grid
{
    public OverlayElement El { get; }
    private readonly ElementContext _ctx;
    private TextBlock? _clock, _clockSec;
    private WrapPanel? _strip;
    private readonly List<System.Windows.Documents.Run> _stripValues = new();
    private System.Windows.Shapes.Ellipse? _stripDot;

    public ElementView(OverlayElement el, ElementContext ctx)
    {
        El = el;
        _ctx = ctx;
        Build();
    }

    private Brush Accent => ThemeManager.BrushOr(El.Accent, "B.Accent");
    private Brush Fore => ThemeManager.BrushOr(El.Foreground, "B.Text");
    private Brush Dim => ThemeManager.Brush("B.Dim");
    private FontFamily Font => El.FontFamily.Length > 0 ? new FontFamily(El.FontFamily) : (FontFamily)Application.Current.Resources["F.Body"];
    private FontFamily Mono => (FontFamily)Application.Current.Resources["F.Mono"];
    private FontFamily Display => El.FontFamily.Length > 0 ? new FontFamily(El.FontFamily) : (FontFamily)Application.Current.Resources["F.Display"];
    private double Fs => Math.Clamp(El.FontSize, 7, 48);

    public void Build()
    {
        Children.Clear();
        _clock = _clockSec = null; _strip = null; _stripValues.Clear(); _stripKey = "";
        _mediaFill = null; _mediaTrack = null; _mediaTime = null;
        // The top bar sizes to its content, so items you add (clock, DEFCON…) are never cut off.
        Width = El.Kind == ElementKind.Strip ? double.NaN : Math.Clamp(El.W, 60, 2400);
        Height = El.H > 0 ? Math.Clamp(El.H, 30, 2000) : double.NaN;
        Opacity = Math.Clamp(El.Opacity, 0.1, 1);

        FrameworkElement content = El.Kind switch
        {
            ElementKind.Strip => BuildStrip(),
            ElementKind.Clock => BuildClock(),
            ElementKind.Timers => BuildTimers(),
            ElementKind.Defcon => BuildDefcon(),
            ElementKind.Intel => BuildIntel(),
            ElementKind.NotePad => BuildPad(),
            ElementKind.InfoCard => BuildCard(),
            ElementKind.Media => BuildMedia(),
            ElementKind.Note => BuildNote(),
            ElementKind.Video => BuildVideo(),
            _ => BuildText(),
        };
        if (El.H > 0 && El.Kind is ElementKind.NotePad or ElementKind.InfoCard or ElementKind.Text or ElementKind.Intel or ElementKind.Note)
            content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

        Children.Add(Frame(content));
        Tick();
    }

    private FrameworkElement Frame(FrameworkElement content)
    {
        double pad = Math.Clamp(El.Padding, 0, 40);
        var bg = ThemeManager.BrushOr(El.Background, "B.Card", Math.Clamp(El.BackgroundOpacity, 0, 1));
        var stroke = El.Border == BorderKind.None ? null : ThemeManager.BrushOr(El.Accent, "B.Line.Strong", El.Accent.Length > 0 ? 0.7 : 1);
        double t = Math.Clamp(El.BorderThickness, 0, 6);
        if (El.Border == BorderKind.Hud)
        {
            bool strip = El.Kind == ElementKind.Strip;
            var f = new HudFrame
            {
                Content = content, Fill = bg, Stroke = stroke, StrokeThickness = Math.Max(0.5, t), ClipContent = El.H > 0,
                Padding = strip ? new Thickness(pad + 2, pad * 0.55, pad + 4, pad * 0.55) : new Thickness(pad, pad * 0.8, pad, pad * 0.9),
                Chamfer = strip ? Math.Clamp(El.CornerRadius + 2, 0, 9) : Math.Clamp(El.CornerRadius + 2, 0, 24),
                ChamferAll = strip, ShowBrackets = !strip, BracketLength = 12,
            };
            f.BracketBrush = Accent;
            return f;
        }
        return new Border
        {
            Child = content, Padding = new Thickness(pad, pad * 0.8, pad, pad * 0.9), Background = bg,
            BorderBrush = stroke, BorderThickness = new Thickness(El.Border == BorderKind.None ? 0 : t),
            CornerRadius = new CornerRadius(El.Border == BorderKind.Rounded ? Math.Clamp(El.CornerRadius, 0, 30) : 0),
            ClipToBounds = El.H > 0,
        };
    }

    private TextBlock T(string text, double size, Brush brush, FontFamily? font = null, bool bold = false) => new()
    {
        Text = text, FontSize = size, Foreground = brush, FontFamily = font ?? Font, TextTrimming = TextTrimming.CharacterEllipsis,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
    };

    private FrameworkElement Header(string fallback, string? right = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, Fs * 0.5), LastChildFill = true };
        if (right is not null) { var r = T(right, Fs * 0.8, Dim, Mono); DockPanel.SetDock(r, Dock.Right); d.Children.Add(r); }
        if (El.Icon.Length > 0)
        {
            var ic = Ui.Icon(El.Icon, Fs * 1.05);
            ic.Foreground = Accent;
            ic.Margin = new Thickness(0, 0, 7, 0);
            d.Children.Add(ic);
        }
        d.Children.Add(T((El.Title.Length > 0 ? El.Title : fallback).ToUpperInvariant(), Fs * 0.92, Accent, Display, bold: true));
        return d;
    }

    private MarkupOptions Markup(int maxLines = int.MaxValue, Action<int>? toggle = null) => new()
    {
        FontSize = Fs, Foreground = Fore, Accent = Accent, Dim = Dim, FontFamily = Font, MonoFamily = Mono,
        MaxLines = maxLines, Interactive = _ctx.Interactive, OnToggleTask = toggle, MaxImageWidth = InnerWidth,
    };

    // ================================================================ kinds

    private FrameworkElement BuildStrip()
    {
        // Items and order come from Settings › Top bar (TopBar.Compose); refreshed every second in Tick().
        // A wrap panel, so it stays one line on the overlay (auto width) and wraps in the companion window.
        _strip = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        FillStrip();
        return _strip;
    }

    private string _stripKey = "";

    /// <summary>Every segment shares this line box, so captions, values and the DEFCON chip sit on one centre line.</summary>
    private double StripLine => Math.Round(Fs * 1.45);

    private FrameworkElement StripSeparator() => new Border
    {
        Width = 1, Height = Math.Round(Fs * 1.1), Margin = new Thickness(Math.Round(Fs * 0.8), 0, Math.Round(Fs * 0.8), 0),
        Background = ThemeManager.Brush("B.Line.Strong"), Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock StripText() => new()
    {
        VerticalAlignment = VerticalAlignment.Center, LineHeight = StripLine, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap,
    };

    private void FillStrip()
    {
        if (_strip is null) return;
        var parts = TopBar.Compose(_ctx.TargetName(), _ctx.SessionText(), SessionTracker.Instance.Running);
        var level = DefconService.Instance.Active;
        // Layout key: what is shown and how it looks, not the values. Values (session time, clock) change
        // every second and only update their text; the bar is rebuilt when an item, colour or tag changes.
        string key = string.Join("|", parts.Select(p => p.Label + "~" + p.Brush + "~" + p.Tag + "~" + p.Bold + "~" + (p.Brush == "defcon" ? level?.Color : "")));
        if (key == _stripKey && _stripValues.Count == parts.Count)
        {
            for (int i = 0; i < parts.Count; i++) if (_stripValues[i].Text != parts[i].Text) _stripValues[i].Text = parts[i].Text;
            return;
        }
        _stripKey = key;
        _strip.Children.Clear();
        _stripValues.Clear();

        _stripDot = new System.Windows.Shapes.Ellipse
        {
            Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Math.Round(Fs * 0.55), 0),
            Fill = ThemeManager.Brush(_ctx.GameAttached() ? "B.Ok" : "B.Amber"),
        };
        _strip.Children.Add(_stripDot);

        if (parts.Count == 0)
        {
            var empty = StripText();
            empty.Inlines.Add(new System.Windows.Documents.Run("TOP BAR · nothing selected") { Foreground = Dim, FontFamily = Mono, FontSize = Fs * 0.8 });
            _strip.Children.Add(empty);
            return;
        }

        double caption = Math.Max(7, Fs * 0.68);
        for (int i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (i > 0) _strip.Children.Add(StripSeparator());

            if (p.Brush == "defcon")
            {
                // A tinted chip in the level's colour: reads at a glance, and the tag (ASSUMED / STALE) sits outside it.
                var col = level is not null ? ThemeManager.BrushOr(level.Color, "B.Accent") : Accent;
                var c = (col as SolidColorBrush)?.Color ?? Colors.White;
                bool unsure = p.Tag.Length > 0;
                var chipText = StripText();
                chipText.LineHeight = Math.Round(Fs * 1.2);
                chipText.Inlines.Add(new System.Windows.Documents.Run(p.Label + " ") { Foreground = col, FontFamily = Mono, FontSize = caption, FontWeight = FontWeights.SemiBold });
                var val = new System.Windows.Documents.Run(p.Text) { Foreground = col, FontFamily = Display, FontSize = Fs, FontWeight = FontWeights.Bold };
                chipText.Inlines.Add(val);
                _stripValues.Add(val);
                var chip = new Border
                {
                    Child = chipText, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(Math.Round(Fs * 0.5), 0, Math.Round(Fs * 0.5), 0),
                    Background = Frozen(Color.FromArgb((byte)(unsure ? 22 : 46), c.R, c.G, c.B)),
                    BorderBrush = Frozen(Color.FromArgb((byte)(unsure ? 90 : 170), c.R, c.G, c.B)), BorderThickness = new Thickness(1),
                };
                _strip.Children.Add(chip);
                if (unsure)
                {
                    var tag = StripText();
                    tag.Margin = new Thickness(Math.Round(Fs * 0.45), 0, 0, 0);
                    tag.Inlines.Add(new System.Windows.Documents.Run(p.Tag.ToUpperInvariant()) { Foreground = Dim, FontFamily = Mono, FontSize = caption });
                    tag.ToolTip = p.Tag == "ASSUMED" ? "Nobody has confirmed this level yet" : "No new information for a while";
                    _strip.Children.Add(tag);
                }
                continue;
            }

            var brush = p.Brush == "B.Text" ? Fore : ThemeManager.Brush(p.Brush);
            var tb = StripText();
            if (p.Label.Length > 0)
                tb.Inlines.Add(new System.Windows.Documents.Run(p.Label + "  ") { Foreground = Dim, FontFamily = Mono, FontSize = caption });
            // Names (game, preset, profile, server) in the display face; numbers (session, clock, date) in mono so they don't jitter.
            bool name = p.Label.Length == 0 && !char.IsDigit(p.Text.FirstOrDefault()) && p.Text != "--:--:--";
            var run = new System.Windows.Documents.Run(p.Text)
            {
                Foreground = brush, FontSize = name ? Fs * 0.95 : Fs,
                FontFamily = name ? Display : Mono,
                FontWeight = name ? FontWeights.SemiBold : FontWeights.Normal,
            };
            tb.Inlines.Add(run);
            _stripValues.Add(run);
            if (p.Tag.Length > 0)
                tb.Inlines.Add(new System.Windows.Documents.Run("  " + p.Tag.ToUpperInvariant()) { Foreground = Dim, FontFamily = Mono, FontSize = caption });
            _strip.Children.Add(tb);
        }
    }

    private static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private FrameworkElement BuildClock()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _clock = T("", Fs, Fore, Display, bold: true);
        _clockSec = T("", Fs * 0.5, Dim, Mono);
        _clockSec.VerticalAlignment = VerticalAlignment.Bottom;
        _clockSec.Margin = new Thickness(4, 0, 0, Fs * 0.12);
        sp.Children.Add(_clock);
        sp.Children.Add(_clockSec);
        return sp;
    }

    private FrameworkElement BuildTimers()
    {
        var sp = new StackPanel();
        int n = TimerService.Instance.Timers.Count;
        sp.Children.Add(Header("TIMERS", n > 0 ? n.ToString() : null));
        var list = new ItemsControl
        {
            ItemsSource = TimerService.Instance.Timers,
            ItemTemplate = (DataTemplate)Application.Current.FindResource("Tpl.Timer"),
            FontSize = Fs,
        };
        sp.Children.Add(list);
        if (n == 0) sp.Children.Add(T("No timers · Ctrl+T", Fs * 0.85, Dim, Mono));
        return sp;
    }

    private FrameworkElement BuildDefcon()
    {
        var sp = new StackPanel();
        sp.Children.Add(Header("DEFCON"));
        var svc = DefconService.Instance;
        var lvl = svc.Active;
        if (!DefconService.Enabled) { sp.Children.Add(T("Turned off for this preset", Fs * 0.85, Dim)); return sp; }

        var color = lvl is null ? Dim : ThemeManager.BrushOr(lvl.Color, "B.Accent");
        var row = new DockPanel();
        var badge = new Border
        {
            MinWidth = Fs * 3.2, Height = Fs * 3.2, BorderBrush = color, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(El.Border == BorderKind.Rounded ? 8 : 2),
            Background = ThemeManager.BrushOr(lvl?.Color ?? "", "B.Accent.Fill", 0.15), Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(4, 0, 4, 0),
            Child = new TextBlock { Text = lvl?.Short ?? "—", FontSize = Fs * 1.7, FontWeight = FontWeights.Bold, Foreground = color, FontFamily = Display, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(T(lvl is null ? "NOT SET" : lvl.Name.ToUpperInvariant(), Fs, lvl is null ? Dim : color, Display, bold: true));
        if (lvl is not null && lvl.Description.Length > 0)
            info.Children.Add(new TextBlock { Text = lvl.Description, FontSize = Fs * 0.85, Foreground = Fore, FontFamily = Font, TextWrapping = TextWrapping.Wrap, MaxHeight = Fs * 2.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        // Trust status: an assumed fallback is never shown as if it were verified. One short line in game;
        // the full explanation lives in the SFHud window.
        if (lvl is not null)
        {
            var status = new TextBlock { FontSize = Fs * 0.72, FontFamily = Mono, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0), ToolTip = svc.StatusDetail };
            status.Inlines.Add(new System.Windows.Documents.Run(svc.StatusLabel) { Foreground = ThemeManager.Brush(svc.StatusBrush), FontWeight = FontWeights.Bold });
            status.Inlines.Add(new System.Windows.Documents.Run("  ·  " + svc.StatusShort) { Foreground = Dim });
            info.Children.Add(status);
        }
        else info.Children.Add(T("Set it in the menu or with a key", Fs * 0.72, Dim, Mono));
        if (svc.Status is DefconStatus.Assumed or DefconStatus.Stale) badge.Opacity = 0.55;
        badge.VerticalAlignment = VerticalAlignment.Top; // lines up with the level name, not the middle of the text block
        row.Children.Add(info);
        sp.Children.Add(row);

        if (svc.Pending is { } p)
        {
            var sug = new Border
            {
                Margin = new Thickness(0, Fs * 0.6, 0, 0), Padding = new Thickness(8, 5, 8, 6), CornerRadius = new CornerRadius(4),
                Background = ThemeManager.BrushOr("", "B.Amber.Fill"), BorderBrush = ThemeManager.Brush("B.Amber"), BorderThickness = new Thickness(1),
            };
            var s = new StackPanel();
            s.Children.Add(T($"SUGGESTED: {p.Level.Label}", Fs * 0.88, ThemeManager.Brush("B.Amber"), Display, bold: true));
            s.Children.Add(T($"{p.Source} · {p.ConfidenceText} sure{(p.FromAi ? " · AI guess" : "")}", Fs * 0.75, Fore, Mono));
            string key = App.Gesture("defcon.accept");
            if (_ctx.Interactive)
            {
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
                buttons.Children.Add(Ui.Button("ACCEPT", () => svc.Accept()));
                buttons.Children.Add(Ui.Button("DISMISS", () => svc.Dismiss()));
                s.Children.Add(buttons);
            }
            else if (key.Length > 0) s.Children.Add(T($"{key} to accept", Fs * 0.72, Dim, Mono));
            sug.Child = s;
            sp.Children.Add(sug);
        }

        if (_ctx.Interactive)
        {
            var levels = new WrapPanel { Margin = new Thickness(0, Fs * 0.6, 0, 0) };
            foreach (var l in DataStore.Preset.Defcon.Levels)
            {
                var b = new Button { Content = l.Short, Style = Ui.Style("Sf.Button.Small"), Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 3, 8, 3), ToolTip = l.Label, Foreground = ThemeManager.BrushOr(l.Color, "B.Accent") };
                string id = l.Id;
                b.Click += (_, _) => svc.SetById(id);
                levels.Children.Add(b);
            }
            sp.Children.Add(levels);
        }
        return sp;
    }

    private FrameworkElement BuildIntel()
    {
        var sp = new StackPanel();
        var intel = IntelService.Instance;
        sp.Children.Add(Header("SERVER INTEL"));
        if (intel.Server.Length + intel.Map.Length > 0)
            sp.Children.Add(T($"{intel.Server}{(intel.Map.Length > 0 ? " · " + intel.Map : "")}", Fs * 0.85, Dim, Mono));
        int shown = 0;
        foreach (var a in intel.ActiveAlerts)
        {
            sp.Children.Add(new TextBlock { Text = $"▲ {a.Time}  {a.Text}", FontSize = Fs * 0.9, Foreground = ThemeManager.Brush("B.Amber"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), FontFamily = Font, MaxHeight = Fs * 4.5, TextTrimming = TextTrimming.CharacterEllipsis });
            shown++;
        }
        if (intel.Orders.Length > 0) { sp.Children.Add(new TextBlock { Text = intel.Orders, FontSize = Fs * 0.9, Foreground = Fore, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontFamily = Font }); shown++; }
        if (intel.Summary.Length > 0) { sp.Children.Add(new TextBlock { Text = intel.Summary, FontSize = Fs * 0.85, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontFamily = Font, FontStyle = FontStyles.Italic }); shown++; }
        if (shown == 0) sp.Children.Add(T(intel.FeedStatus, Fs * 0.8, Dim, Mono));
        return sp;
    }

    private FrameworkElement BuildPad()
    {
        var sp = new StackPanel();
        var pad = DataStore.Preset.Pad(El.PadId) ?? Pads.Current;
        var (open, done) = pad.Notes.Aggregate((0, 0), (acc, n) => { var c = NoteMarkup.TaskCounts(n.Text); return (acc.Item1 + c.Open, acc.Item2 + c.Done); });
        var accent = El.Accent.Length > 0 ? Accent : ThemeManager.BrushOr(pad.Color, "B.Accent");
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, Fs * 0.5) };
        if (open + done > 0) { var r = T($"{done}/{open + done}", Fs * 0.8, Dim, Mono); DockPanel.SetDock(r, Dock.Right); head.Children.Add(r); }
        var ic = Ui.Icon(pad.Icon.Length > 0 ? pad.Icon : "", Fs * 1.05);
        ic.Foreground = accent;
        ic.Margin = new Thickness(0, 0, 7, 0);
        head.Children.Add(ic);
        head.Children.Add(T((El.Title.Length > 0 ? El.Title : pad.Title).ToUpperInvariant(), Fs * 0.92, accent, Display, bold: true));
        if (El.ShowTitle) sp.Children.Add(head);

        var notes = Pads.OverlayNotes(pad, El.MaxItems);
        if (notes.Count == 0) sp.Children.Add(T("Empty · " + (App.Gesture("note.quick") is { Length: > 0 } g ? g + " adds a note" : "add notes from the pad window"), Fs * 0.85, Dim, Mono));
        int maxLines = El.H > 0 ? int.MaxValue : 6;
        for (int i = 0; i < notes.Count; i++)
        {
            var n = notes[i];
            var opts = new MarkupOptions
            {
                FontSize = Fs * pad.FontSize / 13.0, LineSpacing = pad.LineSpacing, Foreground = Fore, Accent = accent, Dim = Dim, FontFamily = Font, MonoFamily = Mono,
                MaxLines = maxLines, Interactive = _ctx.Interactive, OnToggleTask = _ctx.Interactive ? line => Pads.ToggleTask(n, line) : null,
                MaxImageWidth = InnerWidth,
            };
            var body = NoteMarkup.Render(n.Text, opts);
            var row = new DockPanel { Margin = new Thickness(0, i == 0 ? 0 : Fs * 0.45, 0, 0) };
            if (n.LabelColor.Length > 0 || n.Pinned)
            {
                var bar = new Border { Width = 3, Margin = new Thickness(0, 1, 8, 1), CornerRadius = new CornerRadius(1.5), Background = ThemeManager.BrushOr(n.LabelColor, n.Pinned ? "B.Amber" : "B.Accent") };
                DockPanel.SetDock(bar, Dock.Left);
                row.Children.Add(bar);
            }
            if (n.Label.Length > 0)
            {
                var lab = T(n.Label.ToUpperInvariant(), Fs * 0.7, ThemeManager.BrushOr(n.LabelColor, "B.Dim"), Mono, bold: true);
                lab.Margin = new Thickness(0, 0, 0, 2);
                DockPanel.SetDock(lab, Dock.Top);
                row.Children.Add(lab);
            }
            row.Children.Add(body);
            sp.Children.Add(row);
            if (i < notes.Count - 1) sp.Children.Add(new Border { Height = 1, Margin = new Thickness(0, Fs * 0.45, 0, 0), Background = ThemeManager.Brush("B.Line"), Opacity = 0.6 });
        }
        int hidden = pad.Notes.Count(n => !n.HiddenOnOverlay) - notes.Count; // notes hidden on the overlay (e.g. the guide) aren't "more"
        if (hidden > 0) sp.Children.Add(T($"+{hidden} more", Fs * 0.75, Dim, Mono));
        return sp;
    }

    /// <summary>Width inside the frame, so images in notes never spill out of the element.</summary>
    private double InnerWidth => Math.Max(40, Math.Clamp(El.W, 60, 2400) - 2 * Math.Clamp(El.Padding, 0, 40) - 4);

    /// <summary>One note popped out of a pad (the pad window's pop-out button). Follows edits to the note.</summary>
    private FrameworkElement BuildNote()
    {
        var sp = new StackPanel();
        if (Pads.FindNote(El.NoteId) is not { } hit)
        {
            sp.Children.Add(Header("PINNED NOTE"));
            sp.Children.Add(T("That note was deleted. Remove this element, or pop out another note.", Fs * 0.85, Dim));
            return sp;
        }
        var (pad, n) = hit;
        var accent = El.Accent.Length > 0 ? Accent : ThemeManager.BrushOr(n.LabelColor.Length > 0 ? n.LabelColor : pad.Color, "B.Accent");
        if (El.ShowTitle)
        {
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, Fs * 0.45) };
            var right = T(n.Label.ToUpperInvariant(), Fs * 0.72, ThemeManager.BrushOr(n.LabelColor, "B.Dim"), Mono);
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
            var ic = Ui.Icon(pad.Icon.Length > 0 ? pad.Icon : "", Fs);
            ic.Foreground = accent;
            ic.Margin = new Thickness(0, 0, 7, 0);
            head.Children.Add(ic);
            head.Children.Add(T((El.Title.Length > 0 ? El.Title : pad.Title).ToUpperInvariant(), Fs * 0.9, accent, Display, bold: true));
            sp.Children.Add(head);
        }
        sp.Children.Add(NoteMarkup.Render(n.Text, new MarkupOptions
        {
            FontSize = Fs * pad.FontSize / 13.0, LineSpacing = pad.LineSpacing, Foreground = Fore, Accent = accent, Dim = Dim, FontFamily = Font, MonoFamily = Mono,
            Interactive = _ctx.Interactive, OnToggleTask = _ctx.Interactive ? line => Pads.ToggleTask(n, line) : null, MaxImageWidth = InnerWidth,
            OnResizeImage = _ctx.Interactive ? (line, w) => Pads.ResizeImage(n, line, w) : null,
        }));
        return sp;
    }

    private FrameworkElement BuildVideo()
    {
        var sp = new StackPanel();
        if (El.ShowTitle && El.Title.Length > 0) sp.Children.Add(Header("VIDEO"));
        if (!Edition.Has(ProFeature.Video))
        {
            sp.Children.Add(T("Videos on the overlay are part of SFHud Pro", Fs * 0.85, Dim, Mono));
            return sp;
        }
        if (!_ctx.HostsVideo)
        {
            sp.Children.Add(T(El.VideoSource.Length > 0 ? "▶ " + System.IO.Path.GetFileName(El.VideoSource.Split('?')[0]) + " · plays on the overlay" : "No video picked yet", Fs * 0.85, Dim, Mono));
            return sp;
        }
        return sp;
    }

    private FrameworkElement BuildCard()
    {
        var sp = new StackPanel();
        if (El.ShowTitle) sp.Children.Add(Header("CARD"));
        sp.Children.Add(NoteMarkup.Render(El.Text, Markup(toggle: _ctx.Interactive ? line => { El.Text = NoteMarkup.ToggleTask(El.Text, line); DataStore.MarkDirty(); Build(); } : null)));
        return sp;
    }

    private FrameworkElement BuildText() =>
        NoteMarkup.Render(El.Text, Markup(toggle: _ctx.Interactive ? line => { El.Text = NoteMarkup.ToggleTask(El.Text, line); DataStore.MarkDirty(); Build(); } : null));

    private FrameworkElement BuildMedia()
    {
        var m = MediaService.Instance;
        var sp = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, Fs * 0.4) };
        var badge = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1), Background = ThemeManager.BrushOr(El.Accent, "B.Accent.Fill2"), VerticalAlignment = VerticalAlignment.Center };
        badge.Child = T("♪ " + m.ModeLabel + (_ctx.Interactive ? "  ▾" : ""), Fs * 0.68, Accent, Mono, bold: true);
        if (_ctx.Interactive)
        {
            // Customize mode / companion window: click the badge to change what it listens to.
            badge.Cursor = System.Windows.Input.Cursors.Hand;
            badge.ToolTip = "Change source: Auto, Steam browser, Chrome, Brave, Edge, Firefox, Spotify…";
            badge.MouseLeftButtonUp += (_, e) => { e.Handled = true; MediaSourceMenu.Open(badge, Build); };
        }
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        head.Children.Add(T((El.Title.Length > 0 ? El.Title : "NOW PLAYING").ToUpperInvariant(), Fs * 0.85, Accent, Display, bold: true));
        sp.Children.Add(head);

        var row = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (_ctx.Interactive)
        {
            buttons.Children.Add(Ui.IconButton("", "Previous", () => _ = m.PrevAsync()));
            buttons.Children.Add(Ui.IconButton(m.Playing ? "" : "", m.Playing ? "Pause" : "Play", () => _ = m.PlayPauseAsync(), 15));
            buttons.Children.Add(Ui.IconButton("", "Next", () => _ = m.NextAsync()));
        }
        else
        {
            var ic = Ui.Icon(m.Playing ? "" : "", Fs * 1.2);
            ic.Foreground = Accent;
            ic.Margin = new Thickness(0, 0, 4, 0);
            buttons.Children.Add(ic);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(T(m.HasSession && m.Title.Length > 0 ? m.Title : m.Status, Fs, m.HasSession ? Fore : Dim, Font, bold: m.HasSession));
        // The badge already names the app, so one line: artist, plus "paused" when it is (no "via Brave · Playing" repeat).
        string sub = string.Join("  ·  ", new[] { m.Artist, m.HasSession && !m.Playing ? "paused" : "" }.Where(x => x.Length > 0));
        if (sub.Length > 0) info.Children.Add(T(sub, Fs * 0.8, Dim, Font));
        row.Children.Add(info);
        sp.Children.Add(row);
        // Track progress (refreshed each second in Tick). Hidden when the player reports no timeline.
        if (m.HasSession)
        {
            var track = new Grid { Height = Math.Max(2, Fs * 0.22), Margin = new Thickness(0, Fs * 0.45, 0, 0) };
            track.Children.Add(new Border { Background = ThemeManager.Brush("B.Line"), CornerRadius = new CornerRadius(2) });
            _mediaFill = new Border { Background = Accent, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            track.Children.Add(_mediaFill);
            _mediaTrack = track;
            _mediaTime = T("", Fs * 0.68, Dim, Mono);
            _mediaTime.HorizontalAlignment = HorizontalAlignment.Right;
            _mediaTime.Margin = new Thickness(0, 2, 0, 0);
            sp.Children.Add(track);
            sp.Children.Add(_mediaTime);
        }
        // Volume of the player app (the Windows mixer slider for it), adjustable in customize mode / companion.
        if (_ctx.Interactive && m.HasSession && m.Volume is { } vol)
        {
            var vrow = new DockPanel { Margin = new Thickness(0, Fs * 0.35, 0, 0) };
            var vic = Ui.Icon("", Fs * 0.85, "B.Dim");
            vic.Margin = new Thickness(0, 0, 6, 0);
            vic.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(vic, Dock.Left);
            var pct = T($"{vol:P0}", Fs * 0.68, Dim, Mono);
            pct.MinWidth = Fs * 2.6;
            pct.TextAlignment = TextAlignment.Right;
            pct.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(pct, Dock.Right);
            var sl = new Slider { Minimum = 0, Maximum = 1, Value = vol, SmallChange = 0.02, LargeChange = 0.1, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Music volume (only the player app, not the game)" };
            sl.ValueChanged += (_, e) => { m.SetVolume(e.NewValue); pct.Text = $"{e.NewValue:P0}"; };
            vrow.Children.Add(vic);
            vrow.Children.Add(pct);
            vrow.Children.Add(sl);
            sp.Children.Add(vrow);
        }
        if (m.PlayingElsewhere.Length > 0 && m.PlayingElsewhereMode is { } other)
        {
            var hint = T($"{m.PlayingElsewhere} is playing" + (_ctx.Interactive ? " · click to listen" : ""), Fs * 0.68, ThemeManager.Brush("B.Amber"), Mono);
            if (_ctx.Interactive) { hint.Cursor = System.Windows.Input.Cursors.Hand; hint.MouseLeftButtonUp += (_, e) => { e.Handled = true; m.SetMode(other); Build(); }; }
            sp.Children.Add(hint);
        }
        if (!_ctx.Interactive && App.Gesture("media.playpause") is { Length: > 0 } k)
            sp.Children.Add(T($"{k} play / pause", Fs * 0.66, Dim, Mono));
        return sp;
    }

    /// <summary>Per-second refresh for the clock and status strip.</summary>
    public void Tick()
    {
        if (_clock is not null)
        {
            // Same format and time zone as the top bar (Settings › Top bar & clock).
            var now = TopBar.Now();
            var s = DataStore.Settings;
            _clock.Text = TopBar.TimeText(now, seconds: false) + (s.ClockLabel.Trim().Length > 0 ? " " + s.ClockLabel.Trim().ToUpperInvariant() : "");
            _clockSec!.Text = now.ToString("ss");
            _clockSec.Visibility = s.ClockSeconds ? Visibility.Visible : Visibility.Collapsed;
        }
        if (_strip is not null)
        {
            _stripDot!.Fill = ThemeManager.Brush(_ctx.GameAttached() ? "B.Ok" : "B.Amber");
            FillStrip();
        }
        if (_mediaFill is not null && _mediaTrack is not null && _mediaTime is not null)
        {
            if (MediaService.Instance.Timeline() is { } t && t.Length.TotalSeconds > 0)
            {
                _mediaTrack.Visibility = _mediaTime.Visibility = Visibility.Visible;
                _mediaFill.Width = Math.Max(0, _mediaTrack.ActualWidth * Math.Clamp(t.Position.TotalSeconds / t.Length.TotalSeconds, 0, 1));
                bool h = t.Length.TotalHours >= 1;
                string F(TimeSpan x) => h ? $"{(int)x.TotalHours}:{x.Minutes:00}:{x.Seconds:00}" : $"{(int)x.TotalMinutes}:{x.Seconds:00}";
                _mediaTime.Text = $"{F(t.Position)} / {F(t.Length)}";
            }
            else _mediaTrack.Visibility = _mediaTime.Visibility = Visibility.Collapsed; // player doesn't report a timeline
        }
    }

    private Border? _mediaFill;
    private Grid? _mediaTrack;
    private TextBlock? _mediaTime;
}
