using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace SFHud.Services;

public sealed class MarkupOptions
{
    public double FontSize { get; init; } = 13;
    public double LineSpacing { get; init; } = 1.25;
    public Brush Foreground { get; init; } = Brushes.White;
    public Brush Accent { get; init; } = Brushes.DeepSkyBlue;
    public Brush Dim { get; init; } = Brushes.Gray;
    public FontFamily? FontFamily { get; init; }
    public FontFamily? MonoFamily { get; init; }
    public int MaxLines { get; init; } = int.MaxValue;
    /// <summary>Links open and checkboxes toggle (dashboard, pad window, overlay in customize mode).</summary>
    public bool Interactive { get; init; } = true;
    public Action<int>? OnToggleTask { get; init; }
    /// <summary>Image resized by dragging its corner or from its menu (line index, new width; 0 = fit).</summary>
    public Action<int, double>? OnResizeImage { get; init; }
    public Action<int>? OnRemoveImage { get; init; }
    /// <summary>Overlay elements cap images to their own width.</summary>
    public double MaxImageWidth { get; init; } = double.PositiveInfinity;
    /// <summary>Draw into a read-only rich text box so the text can be selected and copied (note pad).</summary>
    public bool Selectable { get; init; }
}

/// <summary>
/// SFHud's note formatting. Plain text that reads fine unrendered, drawn the same way on the
/// dashboard, in pad windows and on the overlay.
///
///   # Heading   ## Heading   ### Heading
///   **bold**  *italic*  __underline__  ~~strike~~  ==highlight==  `code`
///   [color=red]text[/color]   [hl=green]text[/hl]   (names or #RRGGBB)
///   - bullet    1. numbered    - [ ] task    - [x] done    > quote    ---
///   https://links  [label](https://link)   [21:30] timestamps
/// </summary>
public static partial class NoteMarkup
{
    public static readonly (string Name, string Hex)[] Colors =
    {
        ("red", "#FF4D5E"), ("orange", "#FF8A3D"), ("yellow", "#FFD84D"), ("green", "#7CFFB2"), ("cyan", "#00E5FF"),
        ("blue", "#4FA3FF"), ("purple", "#B57BFF"), ("pink", "#FF6FB5"), ("white", "#FFFFFF"), ("gray", "#9AA4B2"),
    };

    [GeneratedRegex(@"^(\s*)(?:[-*]\s+)?\[( |x|X)\]\s?")] private static partial Regex TaskRx();
    [GeneratedRegex(@"^(\s*)(?:todo|task)\s*:?\s+", RegexOptions.IgnoreCase)] private static partial Regex TodoRx();
    [GeneratedRegex(@"^(\s*)[-*•]\s+")] private static partial Regex BulletRx();
    [GeneratedRegex(@"^(\s*)(\d{1,3})[.)]\s+")] private static partial Regex NumberRx();
    [GeneratedRegex(@"^(#{1,3})\s+")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"^\[(\d{1,2}:\d{2}(?::\d{2})?)\]")] private static partial Regex StampRx();
    [GeneratedRegex(@"^https?://[^\s<>\]\)]+", RegexOptions.IgnoreCase)] private static partial Regex UrlRx();
    [GeneratedRegex(@"^\[([^\]\n]+)\]\((https?://[^\s\)]+)\)", RegexOptions.IgnoreCase)] private static partial Regex LinkRx();
    [GeneratedRegex(@"^\[(color|c|hl)=([#a-zA-Z0-9]+)\]", RegexOptions.IgnoreCase)] private static partial Regex OpenTagRx();
    [GeneratedRegex(@"^\[/(color|c|hl)\]", RegexOptions.IgnoreCase)] private static partial Regex CloseTagRx();
    [GeneratedRegex(@"\[/?(?:color|c|hl)(?:=[#a-zA-Z0-9]+)?\]|\*\*|__|~~|==|`", RegexOptions.IgnoreCase)] private static partial Regex StripRx();

    // ================================================================ text helpers

    public static string PlainText(string text)
    {
        if (text.Contains("!["))
            text = string.Join("\n", text.Split('\n').Select(l => NoteImages.Parse(l) is { } img ? $"[image{(img.Caption.Length > 0 ? ": " + img.Caption : "")}]" : l));
        string s = StripRx().Replace(text, "");
        s = Regex.Replace(s, @"\[([^\]\n]+)\]\((https?://[^\s\)]+)\)", "$1");
        s = Regex.Replace(s, @"(?m)^#{1,3}\s+", "");
        s = Regex.Replace(s, @"(?m)^(\s*)(?:[-*]\s+)?\[( |x|X)\]\s?", "$1");
        return s;
    }

    public static bool IsTaskLine(string line, out bool done)
    {
        var m = TaskRx().Match(line);
        if (m.Success) { done = m.Groups[2].Value is "x" or "X"; return true; }
        done = false;
        return TodoRx().IsMatch(line);
    }

    public static (int Open, int Done) TaskCounts(string text)
    {
        int open = 0, done = 0;
        foreach (var l in text.Split('\n'))
            if (IsTaskLine(l, out bool d)) { if (d) done++; else open++; }
        return (open, done);
    }

    /// <summary>Ticks or unticks the task on one line ("todo: x" becomes "- [x] x").</summary>
    public static string ToggleTask(string text, int lineIndex)
    {
        var lines = text.Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length) return text;
        string l = lines[lineIndex];
        var m = TaskRx().Match(l);
        if (m.Success)
        {
            bool done = m.Groups[2].Value is "x" or "X";
            int at = m.Groups[2].Index;
            lines[lineIndex] = l[..at] + (done ? " " : "x") + l[(at + 1)..];
        }
        else
        {
            var t = TodoRx().Match(l);
            if (t.Success) lines[lineIndex] = t.Groups[1].Value + "- [x] " + l[t.Length..];
        }
        return string.Join("\n", lines);
    }

    public static Color? ParseColor(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        foreach (var (name, hex) in Colors)
            if (name.Equals(s, StringComparison.OrdinalIgnoreCase)) s = hex;
        try { return (Color)ColorConverter.ConvertFromString(s.StartsWith('#') ? s : "#" + s); }
        catch (Exception) { try { return (Color)ColorConverter.ConvertFromString(s); } catch (Exception) { return null; } }
    }

    // ================================================================ rendering

    public static FrameworkElement Render(string text, MarkupOptions o)
    {
        if (o.Selectable) return RenderSelectable(text, o);
        var panel = new StackPanel();
        var lines = text.Replace("\r", "").Split('\n');
        int shown = 0;
        bool lastBlank = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0)
            {
                if (!lastBlank && shown > 0) panel.Children.Add(new Border { Height = o.FontSize * 0.45 });
                lastBlank = true;
                continue;
            }
            lastBlank = false;
            if (shown >= o.MaxLines)
            {
                panel.Children.Add(new TextBlock { Text = "…", Foreground = o.Dim, FontSize = o.FontSize });
                break;
            }
            if (NoteImages.Parse(line) is { } img)
            {
                panel.Children.Add(RenderImage(img, i, o));
                shown += 3;
                continue;
            }
            panel.Children.Add(RenderLine(line, i, o));
            shown++;
        }
        return panel;
    }

    /// <summary>
    /// Same formatting as <see cref="Render"/>, but in a read-only RichTextBox: drag to select
    /// across lines, Ctrl+C / Ctrl+A work, links and task boxes still respond.
    /// </summary>
    private static FrameworkElement RenderSelectable(string text, MarkupOptions o)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontSize = o.FontSize,
            Foreground = o.Foreground,
            TextAlignment = TextAlignment.Left,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = o.FontSize * o.LineSpacing + 1,
        };
        if (o.FontFamily is not null) doc.FontFamily = o.FontFamily;

        var lines = text.Replace("\r", "").Split('\n');
        int shown = 0;
        bool lastBlank = false;
        Block? last = null;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0)
            {
                if (!lastBlank && last is not null) { var mg = last.Margin; last.Margin = new Thickness(mg.Left, mg.Top, mg.Right, mg.Bottom + o.FontSize * 0.45); }
                lastBlank = true;
                continue;
            }
            lastBlank = false;
            if (shown >= o.MaxLines)
            {
                doc.Blocks.Add(new Paragraph(new Run("…") { Foreground = o.Dim }) { Margin = new Thickness(0) });
                break;
            }
            if (NoteImages.Parse(line) is { } img)
            {
                last = new BlockUIContainer(RenderImage(img, i, o)) { Margin = new Thickness(0) };
                doc.Blocks.Add(last);
                shown += 3;
                continue;
            }
            last = LineBlock(line, i, o);
            doc.Blocks.Add(last);
            shown++;
        }

        var box = new RichTextBox
        {
            Document = doc,
            IsReadOnly = true,
            IsDocumentEnabled = true,
            IsReadOnlyCaretVisible = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = o.Foreground,
            SelectionBrush = o.Accent,
            SelectionOpacity = 0.35,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null,
            Cursor = Cursors.IBeam,
        };
        // The box has its own ScrollViewer; hand the wheel to the list it sits in.
        box.PreviewMouseWheel += (s, e) =>
        {
            if (s is not UIElement u || VisualTreeHelper.GetParent(u) is not UIElement parent) return;
            e.Handled = true;
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = u });
        };
        return box;
    }

    private static Block LineBlock(string line, int index, MarkupOptions o)
    {
        double size = o.FontSize;
        if (line.Trim() == "---")
            return new BlockUIContainer(new Border { Height = 1, Margin = new Thickness(0, size * 0.4, 0, size * 0.4), Background = o.Dim, Opacity = 0.5 }) { Margin = new Thickness(0) };

        var p = new Paragraph { FontSize = size, LineHeight = size * o.LineSpacing + 1, Margin = new Thickness(0, 0, 0, 1) };
        Brush fg = o.Foreground;
        string body = line;
        double indent = 0, hang = 0;
        Match m;
        if ((m = HeadingRx().Match(line)).Success)
        {
            int level = m.Groups[1].Value.Length;
            p.FontSize = size * (level == 1 ? 1.42 : level == 2 ? 1.22 : 1.08);
            p.FontWeight = FontWeights.SemiBold;
            p.LineHeight = p.FontSize * 1.2 + 1;
            if (level <= 2) fg = o.Accent;
            p.Margin = new Thickness(0, level == 1 ? 2 : 1, 0, 2);
            body = line[m.Length..];
        }
        else if (IsTaskLine(line, out bool done))
        {
            var tm = TaskRx().Match(line);
            if (!tm.Success) tm = TodoRx().Match(line);
            indent = tm.Groups[1].Value.Length * 6;
            body = line[tm.Length..];
            var check = new TextBlock
            {
                Text = done ? "" : "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = size * 0.95,
                Foreground = done ? o.Dim : o.Accent,
                Margin = new Thickness(0, 0, 7, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Arrow,
            };
            if (o.Interactive && o.OnToggleTask is { } toggle)
            {
                check.Cursor = Cursors.Hand;
                check.ToolTip = done ? "Mark as not done" : "Mark as done";
                check.MouseLeftButtonDown += (_, e) => { toggle(index); e.Handled = true; };
            }
            p.Inlines.Add(new InlineUIContainer(check) { BaselineAlignment = BaselineAlignment.Center });
            hang = size * 0.95 + 7;
            if (done) { fg = o.Dim; p.TextDecorations = TextDecorations.Strikethrough; }
        }
        else if ((m = BulletRx().Match(line)).Success)
        {
            indent = m.Groups[1].Value.Length * 6;
            body = line[m.Length..];
            p.Inlines.Add(new Run((indent > 0 ? "◦" : "•") + "  ") { Foreground = o.Accent });
            hang = size * 1.1;
        }
        else if ((m = NumberRx().Match(line)).Success)
        {
            indent = m.Groups[1].Value.Length * 6;
            body = line[m.Length..];
            p.Inlines.Add(new Run(m.Groups[2].Value + ".  ") { Foreground = o.Accent });
            hang = size * 1.5;
        }
        else if (line.StartsWith("> "))
        {
            body = line[2..];
            p.FontStyle = FontStyles.Italic;
            fg = o.Dim;
            p.BorderBrush = o.Accent;
            p.BorderThickness = new Thickness(2, 0, 0, 0);
            p.Padding = new Thickness(9, 1, 0, 1);
        }

        p.Foreground = fg;
        AddInlines(p.Inlines, body, o, fg);
        if (hang > 0) { p.TextIndent = -hang; indent += hang; }
        p.Margin = new Thickness(indent, p.Margin.Top, 0, p.Margin.Bottom);
        return p;
    }

    /// <summary>An image line: picture, optional caption, and (when interactive) a corner grip to resize it.</summary>
    private static FrameworkElement RenderImage(NoteImages.ImageRef img, int index, MarkupOptions o)
    {
        var src = NoteImages.Get(img.File);
        if (src is null)
            return new TextBlock { Text = $"[image missing: {img.File}]", Foreground = o.Dim, FontSize = o.FontSize * 0.9, FontStyle = FontStyles.Italic, Margin = new Thickness(0, 2, 0, 4) };

        double natural = src.PixelWidth;
        var image = new Image { Source = src, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, HorizontalAlignment = HorizontalAlignment.Left };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        void SetWidth(double w)
        {
            if (w > 0) { image.Width = Math.Min(w, o.MaxImageWidth); image.MaxWidth = double.PositiveInfinity; }
            else { image.Width = double.NaN; image.MaxWidth = Math.Min(natural, o.MaxImageWidth); }
        }
        SetWidth(img.Width);

        var frame = new Border { Child = image, BorderThickness = new Thickness(1), BorderBrush = o.Dim, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, ClipToBounds = true, ToolTip = img.Caption.Length > 0 ? img.Caption : null };
        var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 3, 0, 5) };
        host.Children.Add(frame);

        if (o.Interactive && o.OnResizeImage is { } resize)
        {
            var grip = new System.Windows.Controls.Primitives.Thumb
            {
                Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Cursor = Cursors.SizeNWSE, ToolTip = "Drag to resize · right-click for sizes", Opacity = 0,
                Template = GripTemplate(o.Accent),
            };
            double startW = 0;
            grip.DragStarted += (_, _) => startW = image.ActualWidth;
            grip.DragDelta += (_, e) => { startW = Math.Clamp(startW + e.HorizontalChange, 40, Math.Min(4000, o.MaxImageWidth)); image.Width = startW; image.MaxWidth = double.PositiveInfinity; };
            grip.DragCompleted += (_, _) => resize(index, Math.Round(image.Width));
            host.Children.Add(grip);
            host.MouseEnter += (_, _) => grip.Opacity = 1;
            host.MouseLeave += (_, _) => { if (!grip.IsDragging) grip.Opacity = 0; };

            var menu = new ContextMenu();
            void Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); menu.Items.Add(mi); }
            Item("Small (160)", () => resize(index, 160));
            Item("Medium (280)", () => resize(index, 280));
            Item("Large (420)", () => resize(index, 420));
            Item("Fit the note's width", () => resize(index, 0));
            Item("Actual size", () => resize(index, natural));
            menu.Items.Add(new Separator());
            Item("Open image", () => { try { Process.Start(new ProcessStartInfo(NoteImages.PathOf(img.File)) { UseShellExecute = true }); } catch (Exception) { } });
            if (o.OnRemoveImage is { } remove) Item("Remove from note", () => remove(index));
            host.ContextMenu = menu;
        }
        if (img.Caption.Length > 0 && !img.Caption.StartsWith("pasted ", StringComparison.Ordinal))
        {
            var panel = new StackPanel { Margin = host.Margin };
            host.Margin = new Thickness(0);
            panel.Children.Add(host);
            panel.Children.Add(new TextBlock { Text = img.Caption, Foreground = o.Dim, FontSize = o.FontSize * 0.82, Margin = new Thickness(1, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            return panel;
        }
        return host;
    }

    private static ControlTemplate GripTemplate(Brush accent)
    {
        var t = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
        var path = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        path.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 13,3 L 3,13 M 13,7 L 7,13 M 13,11 L 11,13"));
        path.SetValue(System.Windows.Shapes.Shape.StrokeProperty, accent);
        path.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.6);
        var bg = new FrameworkElementFactory(typeof(Border));
        bg.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)));
        bg.SetValue(Border.CornerRadiusProperty, new CornerRadius(3, 0, 3, 0));
        bg.AppendChild(path);
        t.VisualTree = bg;
        return t;
    }

    private static FrameworkElement RenderLine(string line, int index, MarkupOptions o)
    {
        double size = o.FontSize;
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            Foreground = o.Foreground,
            LineHeight = size * o.LineSpacing + 1,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        if (o.FontFamily is not null) tb.FontFamily = o.FontFamily;

        string body = line;
        FrameworkElement? marker = null;
        double indent = 0;

        if (line.Trim() == "---")
            return new Border { Height = 1, Margin = new Thickness(0, size * 0.4, 0, size * 0.4), Background = o.Dim, Opacity = 0.5 };

        Match m;
        if ((m = HeadingRx().Match(line)).Success)
        {
            int level = m.Groups[1].Value.Length;
            tb.FontSize = size * (level == 1 ? 1.42 : level == 2 ? 1.22 : 1.08);
            tb.FontWeight = FontWeights.SemiBold;
            tb.LineHeight = tb.FontSize * 1.2 + 1;
            if (level <= 2) tb.Foreground = o.Accent;
            tb.Margin = new Thickness(0, level == 1 ? 2 : 1, 0, 2);
            body = line[m.Length..];
        }
        else if (IsTaskLine(line, out bool done))
        {
            var tm = TaskRx().Match(line);
            if (!tm.Success) tm = TodoRx().Match(line);
            indent = tm.Groups[1].Value.Length * 6;
            body = line[tm.Length..];
            var box = new TextBlock
            {
                Text = done ? "" : "",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = size * 0.95,
                Foreground = done ? o.Dim : o.Accent,
                Margin = new Thickness(0, size * 0.18, 7, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Background = Brushes.Transparent,
            };
            if (o.Interactive && o.OnToggleTask is { } toggle)
            {
                box.Cursor = Cursors.Hand;
                box.ToolTip = done ? "Mark as not done" : "Mark as done";
                box.MouseLeftButtonDown += (_, e) => { toggle(index); e.Handled = true; };
            }
            marker = box;
            if (done) { tb.Foreground = o.Dim; tb.TextDecorations = TextDecorations.Strikethrough; }
        }
        else if ((m = BulletRx().Match(line)).Success)
        {
            indent = m.Groups[1].Value.Length * 6;
            body = line[m.Length..];
            marker = new TextBlock { Text = indent > 0 ? "◦" : "•", FontSize = size, Foreground = o.Accent, Margin = new Thickness(2, 0, 8, 0) };
        }
        else if ((m = NumberRx().Match(line)).Success)
        {
            indent = m.Groups[1].Value.Length * 6;
            body = line[m.Length..];
            marker = new TextBlock { Text = m.Groups[2].Value + ".", FontSize = size, Foreground = o.Accent, Margin = new Thickness(0, 0, 7, 0), MinWidth = size * 1.2 };
        }
        else if (line.StartsWith("> "))
        {
            body = line[2..];
            tb.FontStyle = FontStyles.Italic;
            tb.Foreground = o.Dim;
            marker = new Border { Width = 2, Background = o.Accent, Margin = new Thickness(0, 1, 9, 1) };
        }

        AddInlines(tb.Inlines, body, o, tb.Foreground);

        if (marker is null)
        {
            tb.Margin = new Thickness(indent, tb.Margin.Top, 0, tb.Margin.Bottom + 1);
            return tb;
        }
        var row = new DockPanel { Margin = new Thickness(indent, 0, 0, 1), LastChildFill = true };
        if (marker is TextBlock mt && o.FontFamily is not null && mt.FontFamily.Source != "Segoe Fluent Icons, Segoe MDL2 Assets") mt.FontFamily = o.FontFamily;
        DockPanel.SetDock(marker, Dock.Left);
        row.Children.Add(marker);
        row.Children.Add(tb);
        return row;
    }

    private sealed record Style(bool Bold, bool Italic, bool Under, bool Strike, bool Code, Brush? Color, Brush? Highlight);

    public static void AddInlines(InlineCollection inlines, string s, MarkupOptions o, Brush baseColor)
    {
        var style = new Style(false, false, false, false, false, null, null);
        var colors = new Stack<Brush?>();
        var highs = new Stack<Brush?>();
        var buf = new StringBuilder();

        void Flush()
        {
            if (buf.Length == 0) return;
            inlines.Add(MakeRun(buf.ToString(), style, o, baseColor));
            buf.Clear();
        }

        bool HasClose(int from, string marker) => s.IndexOf(marker, from, StringComparison.Ordinal) >= 0;

        int i = 0;
        while (i < s.Length)
        {
            string rest = s[i..];
            Match m;
            if (rest.StartsWith('`') && !style.Code && HasClose(i + 1, "`")) { Flush(); style = style with { Code = true }; i++; continue; }
            if (rest.StartsWith('`') && style.Code) { Flush(); style = style with { Code = false }; i++; continue; }
            if (style.Code) { buf.Append(s[i]); i++; continue; }

            if (rest.StartsWith("**") && (style.Bold || HasClose(i + 2, "**"))) { Flush(); style = style with { Bold = !style.Bold }; i += 2; continue; }
            if (rest.StartsWith("__") && (style.Under || HasClose(i + 2, "__"))) { Flush(); style = style with { Under = !style.Under }; i += 2; continue; }
            if (rest.StartsWith("~~") && (style.Strike || HasClose(i + 2, "~~"))) { Flush(); style = style with { Strike = !style.Strike }; i += 2; continue; }
            if (rest.StartsWith("==") && (style.Highlight is not null || HasClose(i + 2, "==")))
            {
                Flush();
                style = style with { Highlight = style.Highlight is null ? Frozen(Color.FromArgb(0x66, 0xFF, 0xD8, 0x4D)) : null };
                i += 2; continue;
            }
            if (rest.StartsWith('*') && !rest.StartsWith("**") && (style.Italic || (rest.Length > 1 && rest[1] != ' ' && HasClose(i + 1, "*"))))
            { Flush(); style = style with { Italic = !style.Italic }; i++; continue; }

            if ((m = OpenTagRx().Match(rest)).Success)
            {
                var c = ParseColor(m.Groups[2].Value);
                Flush();
                if (m.Groups[1].Value.Equals("hl", StringComparison.OrdinalIgnoreCase))
                {
                    highs.Push(style.Highlight);
                    style = style with { Highlight = c is { } hc ? Frozen(Color.FromArgb(0x70, hc.R, hc.G, hc.B)) : style.Highlight };
                }
                else
                {
                    colors.Push(style.Color);
                    style = style with { Color = c is { } cc ? Frozen(cc) : style.Color };
                }
                i += m.Length; continue;
            }
            if ((m = CloseTagRx().Match(rest)).Success)
            {
                Flush();
                if (m.Groups[1].Value.Equals("hl", StringComparison.OrdinalIgnoreCase)) style = style with { Highlight = highs.Count > 0 ? highs.Pop() : null };
                else style = style with { Color = colors.Count > 0 ? colors.Pop() : null };
                i += m.Length; continue;
            }
            if ((m = LinkRx().Match(rest)).Success) { Flush(); inlines.Add(MakeLink(m.Groups[1].Value, m.Groups[2].Value, o)); i += m.Length; continue; }
            if ((i == 0 || !char.IsLetterOrDigit(s[i - 1])) && (m = UrlRx().Match(rest)).Success)
            {
                Flush();
                string url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                inlines.Add(MakeLink(url.Length > 48 ? url[..46] + "…" : url, url, o));
                i += url.Length; continue;
            }
            if ((m = StampRx().Match(rest)).Success)
            {
                Flush();
                inlines.Add(new Run(m.Groups[1].Value) { Foreground = o.Dim, FontFamily = o.MonoFamily ?? new FontFamily("Consolas"), FontSize = o.FontSize * 0.9 });
                i += m.Length; continue;
            }
            buf.Append(s[i]);
            i++;
        }
        Flush();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Run MakeRun(string text, Style st, MarkupOptions o, Brush baseColor)
    {
        var r = new Run(text);
        if (st.Bold) r.FontWeight = FontWeights.Bold;
        if (st.Italic) r.FontStyle = FontStyles.Italic;
        var deco = new TextDecorationCollection();
        if (st.Under) foreach (var d in TextDecorations.Underline) deco.Add(d);
        if (st.Strike) foreach (var d in TextDecorations.Strikethrough) deco.Add(d);
        if (deco.Count > 0) r.TextDecorations = deco;
        if (st.Color is not null) r.Foreground = st.Color;
        if (st.Highlight is not null) r.Background = st.Highlight;
        if (st.Code)
        {
            r.FontFamily = o.MonoFamily ?? new FontFamily("Consolas");
            r.Background ??= Frozen(Color.FromArgb(0x30, 0x80, 0x80, 0x80));
        }
        return r;
    }

    private static Inline MakeLink(string label, string url, MarkupOptions o)
    {
        var link = new Hyperlink(new Run(label)) { Foreground = o.Accent, ToolTip = url };
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            link.NavigateUri = uri;
            link.RequestNavigate += (_, e) =>
            {
                if (!o.Interactive) return;
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch (Exception) { }
                e.Handled = true;
            };
        }
        return link;
    }
}
