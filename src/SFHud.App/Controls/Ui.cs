using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SFHud.Services;

namespace SFHud.Controls;

public sealed class ProgressWidth : IMultiValueConverter
{
    public static readonly ProgressWidth Instance = new();
    public object Convert(object[] v, Type t, object p, CultureInfo c) =>
        v.Length == 2 && v[0] is double a && v[1] is double w ? Math.Max(0, a * w) : 0.0;
    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>
/// Small builders for the code-built settings UI (dashboard pages, overlay menu, editors), so every
/// row, slider, switch and colour picker looks and behaves the same everywhere.
/// </summary>
public static class Ui
{
    public static void Res(FrameworkElement e, DependencyProperty p, string key) => e.SetResourceReference(p, key);
    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static TextBlock Text(string text, double size = 13, string brush = "B.Text", bool wrap = true)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        Res(t, TextBlock.ForegroundProperty, brush);
        Res(t, TextBlock.FontFamilyProperty, "F.Body");
        return t;
    }

    public static TextBlock Label(string text, double size = 10.5)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Res(t, TextBlock.ForegroundProperty, "B.Dim");
        Res(t, TextBlock.FontFamilyProperty, "F.Mono");
        return t;
    }

    public static TextBlock H1(string text) { var t = new TextBlock { Text = text, FontSize = 22, FontWeight = FontWeights.SemiBold }; Res(t, TextBlock.ForegroundProperty, "B.Text"); Res(t, TextBlock.FontFamilyProperty, "F.Display"); return t; }
    public static TextBlock H2(string text, double size = 12.5) { var t = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis }; Res(t, TextBlock.ForegroundProperty, "B.Accent"); Res(t, TextBlock.FontFamilyProperty, "F.Display"); return t; }
    public static TextBlock Hint(string text) { var t = Text(text, 12, "B.Dim"); t.Margin = new Thickness(0, 2, 0, 8); return t; }

    public static TextBlock Icon(string glyph, double size = 14, string brush = "B.Accent")
    {
        var t = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center, FontFamily = (FontFamily)Application.Current.FindResource("F.Icons") };
        Res(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static Button Button(string text, Action onClick, string style = "Sf.Button.Small", string? tip = null)
    {
        // Natural width: in a vertical stack a lone button used to stretch across the whole card.
        var b = new Button { Content = text, Style = Style(style), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Grey hint text shown inside an empty text box (hidden while typing).</summary>
    public static T Placeholder<T>(T box, string text) where T : TextBox
    {
        var hint = new TextBlock
        {
            Text = text, IsHitTestVisible = false, FontSize = 12, Margin = new Thickness(9, 0, 9, 0),
            VerticalAlignment = box.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Res(hint, TextBlock.ForegroundProperty, "B.Muted");
        Res(hint, TextBlock.FontFamilyProperty, "F.Body");
        if (box.AcceptsReturn) hint.Margin = new Thickness(9, 7, 9, 0);
        void Sync() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Sync();
        box.Loaded += (_, _) =>
        {
            if (AdornerLayer.GetAdornerLayer(box) is not { } layer) return;
            foreach (var a in layer.GetAdorners(box) ?? Array.Empty<Adorner>()) if (a is HintAdorner) return;
            layer.Add(new HintAdorner(box, hint));
            Sync();
        };
        return box;
    }

    private sealed class HintAdorner : Adorner
    {
        private readonly TextBlock _hint;
        public HintAdorner(UIElement owner, TextBlock hint) : base(owner) { _hint = hint; IsHitTestVisible = false; AddVisualChild(hint); }
        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _hint;
        protected override Size MeasureOverride(Size constraint) { _hint.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
        protected override Size ArrangeOverride(Size finalSize) { _hint.Arrange(new Rect(finalSize)); return finalSize; }
    }

    public static Button IconButton(string glyph, string tip, Action onClick, double size = 13)
    {
        var b = new Button { Content = glyph, Style = Style("Sf.IconButton"), ToolTip = tip, FontSize = size };
        // Greyscale: ClearType tinted thin glyphs (✕, chevrons) green or blue depending on their pixel position.
        TextOptions.SetTextRenderingMode(b, TextRenderingMode.Grayscale);
        b.Click += (_, e) => { onClick(); e.Handled = true; };
        return b;
    }

    public static CheckBox Switch(string label, bool value, Action<bool> onChange, string? tip = null)
    {
        var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 2, 0, 10), ToolTip = tip };
        c.Click += (_, _) => onChange(c.IsChecked == true);
        return c;
    }

    public static FrameworkElement Slider(string label, double min, double max, double value, Func<double, string> fmt, Action<double> onChange, string? tip = null, double step = 0)
    {
        var val = Label(fmt(value));
        val.HorizontalAlignment = HorizontalAlignment.Right;
        Res(val, TextBlock.ForegroundProperty, "B.Accent");
        var s = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(0, 2, 0, 0) };
        if (step > 0) { s.IsSnapToTickEnabled = true; s.TickFrequency = step; }
        s.ValueChanged += (_, e) => { val.Text = fmt(e.NewValue); onChange(e.NewValue); };
        var head = new DockPanel();
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(Label(label));
        var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 10), ToolTip = tip };
        sp.Children.Add(head);
        sp.Children.Add(s);
        return sp;
    }

    public static TextBox TextBox(string value, Action<string> onCommit, bool live = false, bool multiline = false, double minHeight = 0)
    {
        var t = new TextBox { Text = value, Margin = new Thickness(0, 2, 0, 8), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = minHeight };
        if (multiline) { t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; t.MaxHeight = 320; }
        if (live) t.TextChanged += (_, _) => onCommit(t.Text);
        else
        {
            t.LostKeyboardFocus += (_, _) => onCommit(t.Text);
            if (!multiline) t.KeyDown += (_, e) => { if (e.Key == Key.Enter) { onCommit(t.Text); e.Handled = true; } };
        }
        return t;
    }

    public static ComboBox Combo<T>(IEnumerable<T> items, T? selected, Action<T> onChange, Func<T, string>? display = null)
    {
        var c = new ComboBox { Margin = new Thickness(0, 2, 0, 8) };
        var list = items.ToList();
        foreach (var i in list) c.Items.Add(new ComboBoxItem { Content = display is null ? i?.ToString() : display(i), Tag = i });
        int idx = selected is null ? -1 : list.FindIndex(x => EqualityComparer<T>.Default.Equals(x, selected));
        c.SelectedIndex = idx;
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem { Tag: T v }) onChange(v); };
        return c;
    }

    /// <summary>Label on the left, control on the right; wraps under the label when narrow.</summary>
    public static FrameworkElement Row(string label, FrameworkElement control, string? tip = null, double labelWidth = 150)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 2), ToolTip = tip };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var l = Label(label, 11);
        l.Margin = new Thickness(0, 0, 10, 6);
        l.TextWrapping = TextWrapping.Wrap;
        g.Children.Add(l);
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    // ---------------------------------------------------------------- emoji on tabs and sections

    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji");

    /// <summary>One emoji per tab / section, keyed by its upper-case label. WPF draws them in one colour, so they pick up the theme.</summary>
    private static readonly Dictionary<string, string> TabEmoji = new(StringComparer.OrdinalIgnoreCase)
    {
        ["HOME"] = "🏠", ["NOTE PADS"] = "📝", ["OVERLAY"] = "🖥", ["DEFCON"] = "🚨", ["TIMERS"] = "⏱",
        ["LOCAL AI"] = "🤖", ["PRESETS"] = "🎛", ["PRESETS & PROFILES"] = "🎛", ["GAMES & SERVERS"] = "🎮", ["SETTINGS"] = "⚙",
        ["PAGES & ELEMENTS"] = "🧩", ["KEYBINDS"] = "⌨", ["CROSSHAIR"] = "🎯", ["DISPLAY"] = "🔆",
        ["ELEMENTS"] = "🧩", ["EDIT"] = "✏", ["PAGES"] = "📄",
        ["APPEARANCE"] = "🎨", ["NOTIFICATIONS & SOUNDS"] = "🔔", ["MUSIC PLAYER"] = "🎵", ["BEHAVIOUR"] = "🔧",
        ["SERVER INTEL (CONSOLE.LOG)"] = "📡", ["DATA & BACKUPS"] = "💾", ["ABOUT"] = "ℹ",
        ["SFHUD PRO"] = "⭐",
        ["CHAT ALERTS"] = "💬",
    };

    public static string EmojiFor(string label) => TabEmoji.TryGetValue(label, out var e) ? e : "";

    public static TextBlock Emoji(string emoji, double size = 13)
    {
        var t = new TextBlock { Text = emoji, FontFamily = EmojiFont, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        TextOptions.SetTextRenderingMode(t, TextRenderingMode.Grayscale);
        return t;
    }

    /// <summary>Tab content: the label's emoji (if it has one) followed by the label.</summary>
    public static object TabContent(string label, double emojiSize = 12)
    {
        string e = EmojiFor(label);
        if (e.Length == 0) return label;
        var ic = Emoji(e, emojiSize);
        ic.Margin = new Thickness(0, 0, 7, 0);
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(ic);
        sp.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        return sp;
    }

    public static Expander Section(string title, string subtitle, bool expanded, params UIElement[] content)
    {
        var head = new StackPanel();
        var h2 = H2(title, 12);
        if (EmojiFor(title) is { Length: > 0 } em)
        {
            var ic = Emoji(em, 12);
            ic.Margin = new Thickness(0, 0, 8, 0);
            ic.SetResourceReference(TextBlock.ForegroundProperty, "B.Accent");
            head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { ic, h2 } });
        }
        else head.Children.Add(h2);
        // Right margin keeps long subtitles clear of the expander's chevron.
        if (subtitle.Length > 0) { var s = Text(subtitle, 11.5, "B.Dim"); s.Margin = new Thickness(0, 2, 34, 0); head.Children.Add(s); }
        var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var c in content) body.Children.Add(c);
        return new Expander { Header = head, Content = body, IsExpanded = expanded };
    }

    public static HudFrame Panel(params UIElement[] content)
    {
        var sp = new StackPanel();
        foreach (var c in content) sp.Children.Add(c);
        return new HudFrame { Style = Style("Sf.Panel"), Content = sp, Margin = new Thickness(0, 0, 0, 12) };
    }

    public static StackPanel Stack(params UIElement[] content)
    {
        var sp = new StackPanel();
        foreach (var c in content) sp.Children.Add(c);
        return sp;
    }

    public static WrapPanel Wrap(params UIElement[] content)
    {
        var w = new WrapPanel();
        foreach (var c in content) w.Children.Add(c);
        return w;
    }

    public static StackPanel HStack(params UIElement[] content)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in content) sp.Children.Add(c);
        return sp;
    }

    public static Border Spacer(double h = 10) => new() { Height = h };

    public static SolidColorBrush Brush(string hex)
    {
        var b = new SolidColorBrush(NoteMarkup.ParseColor(hex) ?? Colors.Transparent);
        b.Freeze();
        return b;
    }

    public static readonly string[] Swatches =
    {
        "#00E5FF", "#4FB3FF", "#2F8CFF", "#8C9EFF", "#B57BFF", "#FF6FB5", "#FF2D6F", "#E0313B",
        "#FF8A3D", "#FFC400", "#FFD84D", "#C6FF00", "#7CFFB2", "#A3B26B", "#CDB57E", "#9AA4B2", "#FFFFFF", "#000000",
    };

    /// <summary>Swatches + hex box. With <paramref name="allowDefault"/>, an empty value means "follow the theme".</summary>
    public static FrameworkElement ColorPicker(string hex, Action<string> onChange, bool allowDefault = true)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        var box = new TextBox { Text = hex, Width = 92, Margin = new Thickness(0, 0, 6, 4), FontSize = 12, ToolTip = "Hex colour, e.g. #4FB3FF" };
        var preview = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 4), BorderThickness = new Thickness(1) };
        Res(preview, Border.BorderBrushProperty, "B.Line.Strong");
        void Show(string h) { preview.Background = h.Length == 0 ? null : Brush(h); }
        Show(hex);
        void Commit(string h) { h = h.Trim(); if (h.Length > 0 && NoteMarkup.ParseColor(h) is null) return; box.Text = h; Show(h); onChange(h); }
        box.LostKeyboardFocus += (_, _) => Commit(box.Text);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(box.Text); };
        wrap.Children.Add(preview);
        wrap.Children.Add(box);
        if (allowDefault)
        {
            var d = new Button { Content = "THEME", Style = Style("Sf.Button.Small"), Margin = new Thickness(0, 0, 8, 4), ToolTip = "Follow the theme's colour", Padding = new Thickness(8, 3, 8, 3) };
            d.Click += (_, _) => Commit("");
            wrap.Children.Add(d);
        }
        foreach (var sw in Swatches)
        {
            var b = new Border { Width = 18, Height = 18, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(3), Background = Brush(sw), Cursor = Cursors.Hand, ToolTip = sw, BorderThickness = new Thickness(1) };
            Res(b, Border.BorderBrushProperty, "B.Line");
            string c = sw;
            b.MouseLeftButtonDown += (_, e) => { Commit(c); e.Handled = true; };
            wrap.Children.Add(b);
        }
        return wrap;
    }

    public static FrameworkElement IconPicker(string glyph, Action<string> onChange)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        var tiles = new List<Border>();
        foreach (var (g, name) in IconSet.All)
        {
            var t = new Border { Width = 30, Height = 30, Margin = new Thickness(0, 0, 4, 4), CornerRadius = new CornerRadius(5), Cursor = Cursors.Hand, ToolTip = name, BorderThickness = new Thickness(1), Child = Icon(g, 14, "B.Text") };
            ((TextBlock)t.Child).HorizontalAlignment = HorizontalAlignment.Center;
            void Mark(Border tile, bool on) { tile.SetResourceReference(Border.BorderBrushProperty, on ? "B.Accent" : "B.Line"); tile.SetResourceReference(Border.BackgroundProperty, on ? "B.Accent.Fill2" : "B.Accent.Fill"); }
            Mark(t, g == glyph);
            string gg = g;
            t.MouseLeftButtonDown += (_, e) => { foreach (var x in tiles) Mark(x, x == t); onChange(gg); e.Handled = true; };
            tiles.Add(t);
            wrap.Children.Add(t);
        }
        return wrap;
    }

    // ================================================================ dialogs

    private static Window Dialog(Window? owner, string title, UIElement body, double width = 420)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.Height, Width = width, Topmost = true, ShowInTaskbar = false,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Title = title,
        };
        if (owner is { IsVisible: true }) w.Owner = owner;
        Res(w, Control.FontFamilyProperty, "F.Body");
        var sp = new StackPanel();
        sp.Children.Add(H2(title.ToUpperInvariant(), 13));
        sp.Children.Add(Spacer(10));
        sp.Children.Add(body);
        var frame = new HudFrame { Chamfer = 12, ShowTab = true, Glow = true, Padding = new Thickness(20, 16, 20, 14), Margin = new Thickness(10), Content = sp };
        Res(frame, HudFrame.FillProperty, "B.Bg");
        Res(frame, HudFrame.StrokeProperty, "B.Accent");
        w.Content = frame;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) { w.DialogResult = false; } };
        return w;
    }

    /// <summary>Text input dialog. Returns null when cancelled.</summary>
    public static string? Prompt(Window? owner, string title, string initial = "", string hint = "", bool multiline = false)
    {
        var box = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 120 : 0, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var sp = new StackPanel();
        if (hint.Length > 0) sp.Children.Add(Hint(hint));
        sp.Children.Add(box);
        var ok = new Button { Content = "OK", Style = Style("Sf.Button.Small.Primary"), IsDefault = !multiline, Margin = new Thickness(0, 12, 8, 0) };
        var cancel = new Button { Content = "CANCEL", Style = Style("Sf.Button.Small"), IsCancel = true, Margin = new Thickness(0, 12, 0, 0) };
        sp.Children.Add(HStack(ok, cancel));
        var w = Dialog(owner, title, sp, multiline ? 520 : 420);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { w.Activate(); box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }

    public static bool Confirm(Window? owner, string title, string message, string yes = "DELETE")
    {
        var sp = new StackPanel();
        sp.Children.Add(Text(message, 13, "B.Text"));
        var ok = new Button { Content = yes, Style = Style("Sf.Button.Small.Danger"), Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 14, 8, 0) };
        var cancel = new Button { Content = "CANCEL", Style = Style("Sf.Button.Small"), IsCancel = true, IsDefault = true, Margin = new Thickness(0, 14, 0, 0) };
        sp.Children.Add(HStack(ok, cancel));
        var w = Dialog(owner, title, sp);
        ok.Click += (_, _) => w.DialogResult = true;
        w.KeyDown += (_, e) => { if (e.Key == Key.Y) w.DialogResult = true; if (e.Key == Key.N) w.DialogResult = false; };
        w.Loaded += (_, _) => w.Activate();
        return w.ShowDialog() == true;
    }

    public static void Info(Window? owner, string title, string message)
    {
        var sp = new StackPanel();
        sp.Children.Add(Text(message, 13, "B.Text"));
        var ok = new Button { Content = "OK", Style = Style("Sf.Button.Small.Primary"), IsDefault = true, IsCancel = true, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        sp.Children.Add(ok);
        var w = Dialog(owner, title, sp);
        ok.Click += (_, _) => w.DialogResult = true;
        w.ShowDialog();
    }

    /// <summary>Builds a context menu from (header, action) pairs; null header = separator.</summary>
    public static ContextMenu Menu(params (string? Header, Action? Action)[] items)
    {
        var m = new ContextMenu();
        foreach (var (h, a) in items)
        {
            if (h is null) { m.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = h };
            if (a is not null) mi.Click += (_, _) => a();
            m.Items.Add(mi);
        }
        return m;
    }

    public static MenuItem Sub(string header, params (string Header, Action Action)[] items)
    {
        var mi = new MenuItem { Header = header };
        foreach (var (h, a) in items) { var x = new MenuItem { Header = h }; x.Click += (_, _) => a(); mi.Items.Add(x); }
        return mi;
    }

    /// <summary>Opens files with a save dialog. Returns null if cancelled.</summary>
    public static string? SaveFile(string title, string defaultName, string filter)
    {
        var d = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = defaultName, Filter = filter };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public static string? OpenFile(string title, string filter)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
        return d.ShowDialog() == true ? d.FileName : null;
    }

    public static string SafeFileName(string s) => string.Concat(s.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();

    // ---------------------------------------------------------------- SFHud Pro

    /// <summary>True if the feature is unlocked; otherwise opens the SFHud Pro page and returns false.</summary>
    public static bool RequirePro(ProFeature f)
    {
        if (Edition.Has(f)) return true;
        Edition.ShowUpgrade?.Invoke(f);
        return false;
    }

    /// <summary>Small amber "★ PRO" tag that opens the SFHud Pro page. Collapsed when the feature is unlocked.</summary>
    public static FrameworkElement ProChip(ProFeature f, string? text = null)
    {
        var t = new TextBlock { Text = text ?? "★ PRO", FontSize = 9.5, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "B.Amber");
        t.SetResourceReference(TextBlock.FontFamilyProperty, "F.Mono");
        var b = new Border
        {
            Child = t, Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(6, 0, 0, 0), CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = $"{Edition.Describe(f)} is part of SFHud Pro (one-time purchase). Click to see what Pro adds.",
            Visibility = Edition.Has(f) ? Visibility.Collapsed : Visibility.Visible,
        };
        b.SetResourceReference(Border.BorderBrushProperty, "B.Amber");
        b.SetResourceReference(Border.BackgroundProperty, "B.Amber.Fill");
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; Edition.ShowUpgrade?.Invoke(f); };
        return b;
    }

    /// <summary>How see-through a locked Pro option is in SFHud Free.</summary>
    public const double ProFadeOpacity = 0.4;

    /// <summary>Wraps a control: when the feature is locked it's faded out, can't be changed, and clicking it
    /// (or its PRO chip) opens the SFHud Pro page.</summary>
    public static FrameworkElement ProLock(FrameworkElement control, ProFeature f)
    {
        if (Edition.Has(f)) return control;
        control.IsEnabled = false;
        control.IsHitTestVisible = false;
        control.Opacity = ProFadeOpacity;
        var d = new DockPanel
        {
            LastChildFill = true, Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = $"{Edition.Describe(f)} is part of SFHud Pro. Click to see what Pro adds.",
        };
        d.MouseLeftButtonUp += (_, e) => { e.Handled = true; Edition.ShowUpgrade?.Invoke(f); };
        var chip = ProChip(f);
        DockPanel.SetDock(chip, Dock.Right);
        d.Children.Add(chip);
        d.Children.Add(control);
        return d;
    }

    /// <summary>Fades a button or row that leads to a Pro feature (its own click already calls
    /// <see cref="RequirePro"/>, which opens the Pro page). <paramref name="locked"/> lets callers fade only
    /// when a limit is reached, e.g. the 6th page.</summary>
    public static T ProFade<T>(T e, ProFeature f, bool locked = true) where T : FrameworkElement
    {
        if (!locked || Edition.Has(f)) return e;
        e.Opacity = ProFadeOpacity;
        e.ToolTip = $"★ SFHud Pro · {Edition.Describe(f)}. Click to see what Pro adds.";
        if (e is Button { Content: string s } b && !s.Contains('★') && s.Length > 2) b.Content = s + "  ★";
        return e;
    }

    /// <summary>A card body that replaces a locked feature: what it does and a button to the Pro page.</summary>
    public static FrameworkElement ProPanel(ProFeature f, string pitch)
    {
        var sp = new StackPanel();
        sp.Children.Add(HStack(Text(Edition.Describe(f), 13, "B.Text", wrap: false), ProChip(f)));
        sp.Children.Add(Hint(pitch));
        sp.Children.Add(Button($"SEE SFHUD PRO · {Edition.PriceLifetime} OR {Edition.PriceMonthly}/MO", () => Edition.ShowUpgrade?.Invoke(f), "Sf.Button.Small.Primary"));
        return sp;
    }
}
