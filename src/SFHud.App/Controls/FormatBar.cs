using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SFHud.Services;

namespace SFHud.Controls;

/// <summary>
/// Formatting toolbar for any TextBox holding note markup. Wraps the selection (or inserts a
/// template at the caret) and supports Ctrl+B / Ctrl+I / Ctrl+U shortcuts on the box.
/// </summary>
public static class FormatBar
{
    public static FrameworkElement For(TextBox box, bool compact = false)
    {
        var w = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        void Add(string glyphOrText, string tip, Action a, bool isText = false)
        {
            var b = Ui.IconButton(glyphOrText, tip, () => { a(); box.Focus(); });
            if (isText) { b.FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["F.Body"]; b.FontWeight = FontWeights.Bold; b.FontSize = 12; }
            w.Children.Add(b);
        }
        Add("", "Bold (Ctrl+B)", () => Wrap(box, "**", "**"));
        Add("", "Italic (Ctrl+I)", () => Wrap(box, "*", "*"));
        Add("", "Underline (Ctrl+U)", () => Wrap(box, "__", "__"));
        Add("", "Strikethrough", () => Wrap(box, "~~", "~~"));
        var color = Ui.IconButton("", "Text colour", () => { });
        color.Click += (_, _) => ColorMenu(box, "color", color).IsOpen = true;
        w.Children.Add(color);
        var hl = Ui.IconButton("", "Highlight", () => { });
        hl.Click += (_, _) => ColorMenu(box, "hl", hl).IsOpen = true;
        w.Children.Add(hl);
        Add("H1", "Heading", () => LinePrefix(box, "# "), isText: true);
        Add("H2", "Sub-heading", () => LinePrefix(box, "## "), isText: true);
        Add("", "Bullet list", () => LinePrefix(box, "- "));
        Add("", "Numbered list", () => Numbered(box));
        Add("", "Checkbox / task", () => LinePrefix(box, "- [ ] "));
        if (!compact)
        {
            Add("", "Link", () => Wrap(box, "[", "](https://)"));
            Add("", "Insert timestamp", () => Insert(box, $"[{DateTime.Now:HH:mm}] "));
            Add("", "Divider line", () => Insert(box, "\n---\n"));
            Add("", "Paste clipboard", () => { if (Clipboard.ContainsText()) Insert(box, Clipboard.GetText()); });
        }
        box.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.B) { Wrap(box, "**", "**"); e.Handled = true; }
            else if (e.Key == Key.I) { Wrap(box, "*", "*"); e.Handled = true; }
            else if (e.Key == Key.U) { Wrap(box, "__", "__"); e.Handled = true; }
        };
        return w;
    }

    private static ContextMenu ColorMenu(TextBox box, string tag, FrameworkElement target)
    {
        var m = new ContextMenu { PlacementTarget = target };
        foreach (var (name, hex) in NoteMarkup.Colors)
        {
            var item = new MenuItem { Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = Ui.Brush(hex), Margin = new Thickness(0, 0, 8, 0) }, Ui.Text(name, 12.5) } } };
            string n = name;
            item.Click += (_, _) => { Wrap(box, $"[{tag}={n}]", $"[/{tag}]"); box.Focus(); };
            m.Items.Add(item);
        }
        if (tag == "hl")
        {
            var quick = new MenuItem { Header = "Quick highlight (==text==)" };
            quick.Click += (_, _) => Wrap(box, "==", "==");
            m.Items.Insert(0, quick);
        }
        return m;
    }

    public static void Wrap(TextBox box, string before, string after)
    {
        int s = box.SelectionStart, l = box.SelectionLength;
        string sel = box.SelectedText;
        box.SelectedText = before + sel + after;
        box.SelectionStart = s + before.Length;
        box.SelectionLength = l;
    }

    public static void Insert(TextBox box, string text)
    {
        int s = box.CaretIndex;
        box.SelectedText = text;
        box.SelectionLength = 0;
        box.CaretIndex = s + text.Length;
    }

    private static (int Start, int End) LineRange(TextBox box)
    {
        string t = box.Text;
        int s = box.SelectionStart, e = s + box.SelectionLength;
        int ls = s == 0 ? 0 : t.LastIndexOf('\n', Math.Max(0, s - 1)) + 1;
        if (s > 0 && t[s - 1] == '\n') ls = s;
        int le = t.IndexOf('\n', e);
        return (ls, le < 0 ? t.Length : le);
    }

    public static void LinePrefix(TextBox box, string prefix)
    {
        var (ls, le) = LineRange(box);
        string t = box.Text;
        var lines = t[ls..le].Split('\n');
        bool all = lines.All(l => l.StartsWith(prefix));
        string replaced = string.Join("\n", lines.Select(l => all ? l[prefix.Length..] : prefix + StripPrefix(l)));
        box.Text = t[..ls] + replaced + t[le..];
        box.CaretIndex = ls + replaced.Length;
    }

    private static string StripPrefix(string l)
    {
        foreach (var p in new[] { "- [ ] ", "- [x] ", "### ", "## ", "# ", "- " }) if (l.StartsWith(p)) return l[p.Length..];
        return l;
    }

    private static void Numbered(TextBox box)
    {
        var (ls, le) = LineRange(box);
        string t = box.Text;
        var lines = t[ls..le].Split('\n');
        string replaced = string.Join("\n", lines.Select((l, i) => $"{i + 1}. {StripPrefix(l)}"));
        box.Text = t[..ls] + replaced + t[le..];
        box.CaretIndex = ls + replaced.Length;
    }
}
