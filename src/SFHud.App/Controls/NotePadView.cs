using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SFHud.Services;

namespace SFHud.Controls;

/// <summary>
/// Tabbed note pads: tabs (drag to reorder, right-click to manage), search, formatted notes, and an
/// editor with a formatting toolbar. Used in the floating pad window and on the dashboard's Notes page.
/// </summary>
public sealed class NotePadView : DockPanel
{
    private readonly WrapPanel _tabs = new();
    private readonly StackPanel _notes = new();
    private readonly TextBox _search = new() { Margin = new Thickness(0), ToolTip = "Search every pad in this preset" };
    private readonly ComboBox _category = new() { MinWidth = 110, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Show pads from one category" };
    private readonly TextBox _editor = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _editorState = Ui.Label("");
    private readonly Button _saveButton;
    private readonly Border _previewHost = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(5) };
    private readonly ScrollViewer _scroll;
    private readonly DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private PadNote? _editing;
    private string _filter = "";
    private bool _loading;
    private Point _dragStart;
    private NotePad? _dragPad;

    /// <summary>Asked by the window to move / resize itself when the tab changes.</summary>
    public event Action<NotePad>? PadSelected;
    public Window? HostWindow { get; set; }

    /// <summary>Set by the main window: shows / hides one note on the overlay. Returns true if it's now shown.</summary>
    public static Func<PadNote, bool>? PopOut { get; set; }
    public static Func<PadNote, bool>? IsPoppedOut { get; set; }

    public NotePadView()
    {
        LastChildFill = true;
        _previewHost.SetResourceReference(Border.BackgroundProperty, "B.Accent.Fill");

        // Tabs
        var tabScroll = new ScrollViewer { Content = _tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 96, Margin = new Thickness(0, 0, 0, 6) };
        SetDock(tabScroll, Dock.Top);
        Children.Add(tabScroll);

        // Toolbar: search, category, actions
        var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };
        actions.Children.Add(Ui.IconButton("", "Undo last delete / clear", UndoDelete));
        actions.Children.Add(Ui.IconButton("", "Copy the whole pad", () => { Clipboard.SetText(Pads.PadAsText(Pads.Current)); Flash("Pad copied"); }));
        actions.Children.Add(Ui.IconButton("", "Paste clipboard as a new note", PasteClipboard));
        actions.Children.Add(Ui.IconButton("", "Pad options", () => PadMenu(Pads.Current).IsOpen = true));
        SetDock(actions, Dock.Right);
        tools.Children.Add(actions);
        SetDock(_category, Dock.Right);
        tools.Children.Add(_category);
        _search.TextChanged += (_, _) => { _filter = _search.Text.Trim(); RenderNotes(); };
        var searchWrap = new Grid();
        searchWrap.Children.Add(_search);
        var glass = Ui.Icon("", 11, "B.Dim");
        glass.Margin = new Thickness(0, 0, 8, 0);
        var ph = Ui.HStack(glass, Ui.Label("SEARCH ALL PADS"));
        ph.IsHitTestVisible = false;
        ph.VerticalAlignment = VerticalAlignment.Center;
        ph.Margin = new Thickness(11, 0, 0, 0);
        _search.TextChanged += (_, _) => ph.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchWrap.Children.Add(ph);
        tools.Children.Add(searchWrap);
        SetDock(tools, Dock.Top);
        Children.Add(tools);
        _category.SelectionChanged += (_, _) => { if (!_loading) RenderTabs(); };

        // Editor
        var editor = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _editor.SetResourceReference(Control.FontFamilyProperty, "F.Body");
        _editor.FontSize = 13.5;
        editor.Children.Add(FormatBar.For(_editor));
        editor.Children.Add(_editor);
        editor.Children.Add(_previewHost);
        var foot = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        _saveButton = Ui.Button("ADD NOTE", Commit, "Sf.Button.Small.Primary", "Ctrl+Enter");
        _saveButton.Margin = new Thickness(6, 0, 0, 0);
        var cancel = Ui.Button("CANCEL", CancelEdit);
        cancel.Margin = new Thickness(6, 0, 0, 0);
        var preview = new ToggleButton { Content = "PREVIEW", Style = Ui.Style("Sf.Chip"), Margin = new Thickness(0), ToolTip = "See the formatting as it will look" };
        preview.Click += (_, _) => { _previewHost.Visibility = preview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; RenderPreview(); };
        SetDock(_saveButton, Dock.Right); foot.Children.Add(_saveButton);
        SetDock(cancel, Dock.Right); foot.Children.Add(cancel);
        SetDock(preview, Dock.Right); foot.Children.Add(preview);
        foot.Children.Add(_editorState);
        editor.Children.Add(foot);
        SetDock(editor, Dock.Bottom);
        Children.Add(editor);

        // Images: toolbar button, Ctrl+V with a picture on the clipboard, or drop image files on the editor.
        var imgBar = (WrapPanel)editor.Children[0];
        imgBar.Children.Add(Ui.IconButton("", "Insert image (or paste one with Ctrl+V, or drop a file here). Drag its corner to resize.", InsertImageFromFile));
        _editor.AllowDrop = true;
        _editor.PreviewDragOver += (_, e) => { if (ImageFiles(e.Data).Length > 0) { e.Effects = DragDropEffects.Copy; e.Handled = true; } };
        _editor.PreviewDrop += (_, e) =>
        {
            var files = ImageFiles(e.Data);
            if (files.Length == 0) return;
            e.Handled = true;
            foreach (var f in files.Take(8)) InsertImage(() => NoteImages.Import(f));
        };
        _editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !Clipboard.ContainsText() && NoteImages.ClipboardHasImage())
            {
                InsertImage(() => NoteImages.FromClipboard() ?? throw new InvalidDataException("The clipboard picture couldn't be read."));
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape && _editing is not null) { CancelEdit(); e.Handled = true; }
        };
        _editor.TextChanged += (_, _) =>
        {
            if (_loading) return;
            RenderPreview();
            if (_editing is null) { _draftTimer.Stop(); _draftTimer.Start(); _editorState.Text = "DRAFT…"; }
        };
        _draftTimer.Tick += (_, _) =>
        {
            _draftTimer.Stop();
            var pad = Pads.Current;
            if (pad.Draft != _editor.Text) { pad.Draft = _editor.Text; DataStore.MarkDirty(); }
            _editorState.Text = _editor.Text.Length > 0 ? "DRAFT SAVED" : "";
        };

        // Notes
        _scroll = new ScrollViewer { Content = _notes, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 6, 0) };
        Children.Add(_scroll);

        // Subscribed only while on screen, so closed pad windows are not kept alive by the store.
        Loaded += (_, _) => { DataStore.NotesChanged -= OnNotesChanged; DataStore.NotesChanged += OnNotesChanged; DataStore.ContextChanged -= OnContextChanged; DataStore.ContextChanged += OnContextChanged; Refresh(); };
        Unloaded += (_, _) => { FlushDraft(); DataStore.NotesChanged -= OnNotesChanged; DataStore.ContextChanged -= OnContextChanged; };
    }

    /// <summary>Preset or profile switched: the pads are different objects now.</summary>
    private void OnContextChanged()
    {
        _draftTimer.Stop(); // a pending draft save must not land in the new preset's pad
        Dispatcher.BeginInvoke(() =>
        {
            _editing = null;
            _saveButton.Content = "ADD NOTE";
            Refresh();
            PadSelected?.Invoke(Pads.Current);
        }, DispatcherPriority.Background);
    }

    private void OnNotesChanged()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(() => { RenderTabs(); RenderNotes(); }, DispatcherPriority.Background);
    }

    public void Refresh()
    {
        _loading = true;
        var cats = new[] { "All categories" }.Concat(DataStore.Preset.Categories).ToList();
        string? sel = (_category.SelectedItem as ComboBoxItem)?.Content as string;
        _category.Items.Clear();
        foreach (var c in cats) _category.Items.Add(new ComboBoxItem { Content = c });
        _category.SelectedIndex = Math.Max(0, cats.IndexOf(sel ?? ""));
        _loading = false;
        RenderTabs();
        LoadDraft();
        RenderNotes();
    }

    public void FocusEditor() { _editor.Focus(); Keyboard.Focus(_editor); _editor.CaretIndex = _editor.Text.Length; }

    public void FocusSearch() { _search.Focus(); Keyboard.Focus(_search); }

    private void LoadDraft()
    {
        if (_editing is not null) return;
        _loading = true;
        _editor.Text = Pads.Current.Draft;
        _editorState.Text = _editor.Text.Length > 0 ? "DRAFT RESTORED" : "CTRL+ENTER SAVES";
        _loading = false;
        RenderPreview();
    }

    public void FlushDraft()
    {
        if (_editing is not null || _loading) return;
        _draftTimer.Stop();
        var pad = Pads.Current;
        if (pad.Draft != _editor.Text) { pad.Draft = _editor.Text; DataStore.MarkDirty(); }
    }

    private void Flash(string text) => _editorState.Text = text.ToUpperInvariant();

    private static string[] ImageFiles(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => NoteImages.Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray()
            : Array.Empty<string>();

    private void InsertImageFromFile()
    {
        var path = Ui.OpenFile("Insert an image", NoteImages.FileFilter);
        if (path is not null) InsertImage(() => NoteImages.Import(path));
    }

    /// <summary>Puts an image line at the caret, on its own line, default 320 wide.</summary>
    private void InsertImage(Func<string> make)
    {
        string line;
        try { line = make(); }
        catch (Exception ex) { Flash(ex.Message); return; }
        line = NoteImages.Parse(line) is { } r && r.Width <= 0 ? NoteImages.Line(r.File, 320, r.Caption) : line;
        int at = _editor.CaretIndex;
        string t = _editor.Text;
        string before = at > 0 && t[at - 1] != '\n' ? "\n" : "";
        string after = at < t.Length && t[at] != '\n' ? "\n" : at == t.Length ? "\n" : "";
        _editor.Select(at, 0);
        _editor.SelectedText = before + line + after;
        _editor.CaretIndex = at + before.Length + line.Length + after.Length;
        if (_previewHost.Visibility != Visibility.Visible) Flash("Image added · it shows when you save the note");
        _editor.Focus();
    }

    // ================================================================ tabs

    private void RenderTabs()
    {
        _tabs.Children.Clear();
        string cat = (_category.SelectedItem as ComboBoxItem)?.Content as string ?? "";
        var cur = Pads.Current;
        foreach (var pad in Pads.Visible().Where(p => cat.Length == 0 || cat == "All categories" || p.Category == cat))
        {
            var p = pad;
            bool active = p.Id == cur.Id;
            var accent = ThemeManager.BrushOr(p.Color, "B.Accent");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (p.Pinned) { var pin = Ui.Icon("", 10, "B.Amber"); pin.Margin = new Thickness(0, 0, 5, 0); row.Children.Add(pin); }
            var ic = Ui.Icon(p.Icon.Length > 0 ? p.Icon : "", 12);
            ic.Foreground = accent;
            ic.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(ic);
            var t = Ui.Text(p.Title, 12, active ? "B.Text" : "B.Dim", wrap: false);
            t.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            row.Children.Add(t);
            if (p.Notes.Count > 0) { var n = Ui.Label(" " + p.Notes.Count, 10); n.Margin = new Thickness(5, 0, 0, 0); row.Children.Add(n); }
            if (p.DefconLevelId.Length > 0 && DataStore.Preset.Defcon.Levels.FirstOrDefault(l => l.Id == p.DefconLevelId) is { } lvl)
            { var d = Ui.Label(" DC" + lvl.Short, 9.5); d.Foreground = ThemeManager.BrushOr(lvl.Color, "B.Accent"); row.Children.Add(d); }

            var tab = new HudFrame
            {
                Content = row, Padding = new Thickness(11, 5, 11, 6), Margin = new Thickness(0, 0, 5, 5), Chamfer = 6, ShowBrackets = false,
                ShowTab = active, TabBrush = accent, Glow = active, Cursor = Cursors.Hand, AllowDrop = true,
                ToolTip = $"{p.Title} · {p.Category}{(App.Gesture("pad.open." + p.Id) is { Length: > 0 } g ? " · " + g : "")}\nDrag to reorder · right-click for options",
            };
            tab.Stroke = active ? accent : ThemeManager.Brush("B.Line");
            tab.SetResourceReference(HudFrame.FillProperty, active ? "B.Accent.Fill2" : "B.Accent.Fill");
            tab.MouseLeftButtonDown += (_, e) => { _dragStart = e.GetPosition(this); _dragPad = p; };
            tab.MouseLeftButtonUp += (_, _) => { if (_dragPad == p) { _dragPad = null; SelectPad(p); } };
            tab.MouseMove += (_, e) =>
            {
                if (_dragPad != p || e.LeftButton != MouseButtonState.Pressed) return;
                if ((e.GetPosition(this) - _dragStart).Length < 6) return;
                _dragPad = null;
                DragDrop.DoDragDrop(tab, new DataObject("sfhud-pad", p.Id), DragDropEffects.Move);
            };
            tab.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent("sfhud-pad") ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            tab.Drop += (_, e) =>
            {
                if (e.Data.GetData("sfhud-pad") is string id && DataStore.Preset.Pad(id) is { } moving && moving != p)
                {
                    if (moving.Pinned != p.Pinned) moving.Pinned = p.Pinned;
                    Pads.Move(moving, p);
                }
            };
            tab.MouseRightButtonUp += (_, e) => { PadMenu(p).IsOpen = true; e.Handled = true; };
            _tabs.Children.Add(tab);
        }
        var add = Ui.IconButton("", "New note pad", NewPad);
        add.Margin = new Thickness(2, 0, 0, 4);
        _tabs.Children.Add(add);
    }

    public void SelectPad(NotePad p)
    {
        FlushDraft();
        _editing = null;
        _saveButton.Content = "ADD NOTE";
        Pads.Select(p);
        LoadDraft();
        PadSelected?.Invoke(p);
    }

    private void NewPad()
    {
        string? name = Ui.Prompt(HostWindow, "New note pad", "Notes");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            string cat = (_category.SelectedItem as ComboBoxItem)?.Content as string ?? "General";
            var pad = Pads.Create(name.Trim(), cat == "All categories" ? "General" : cat);
            SelectPad(pad);
        }
        catch (InvalidOperationException ex) { Ui.Info(HostWindow, "Note pads", ex.Message); }
    }

    private ContextMenu PadMenu(NotePad p)
    {
        var pre = DataStore.Preset;
        var m = Ui.Menu(
            ("Rename…", () => { if (Ui.Prompt(HostWindow, "Rename pad", p.Title) is { Length: > 0 } n) { p.Title = n.Trim(); DataStore.NotifyNotes(); DataStore.NotifyContext(); } }),
            ("Change icon…", () => PickIcon(p)),
            (p.Pinned ? "Unpin tab" : "Pin tab", () => { p.Pinned = !p.Pinned; DataStore.NotifyNotes(); }),
            ("Move left", () => Pads.Shift(p, -1)),
            ("Move right", () => Pads.Shift(p, +1)),
            ("Duplicate", () => SelectPad(Pads.Duplicate(p))),
            (null, null),
            ("Export pad…", () => ExportPad(p)),
            ("Import pad…", ImportPad),
            ("Import document (.txt / .md)…", ImportDocument),
            (null, null),
            ("Clear all notes…", () => ClearPad(p)),
            ("Delete pad…", () => DeletePad(p)));

        var colour = new MenuItem { Header = "Colour" };
        foreach (var (name, hex) in new[] { ("Theme", "") }.Concat(NoteMarkup.Colors))
        {
            var mi = new MenuItem { Header = name };
            string h = hex;
            mi.Click += (_, _) => { p.Color = h; DataStore.NotifyNotes(); };
            colour.Items.Add(mi);
        }
        m.Items.Insert(2, colour);

        var cat = new MenuItem { Header = $"Category ({p.Category})" };
        foreach (var c in pre.Categories)
        {
            var mi = new MenuItem { Header = c };
            string cc = c;
            mi.Click += (_, _) => { p.Category = cc; DataStore.NotifyNotes(); };
            cat.Items.Add(mi);
        }
        var newCat = new MenuItem { Header = "New category…" };
        newCat.Click += (_, _) =>
        {
            if (Ui.Prompt(HostWindow, "New category", "") is not { Length: > 0 } c) return;
            c = c.Trim();
            if (!pre.Categories.Contains(c)) pre.Categories.Add(c);
            p.Category = c;
            DataStore.NotifyNotes();
            Refresh();
        };
        cat.Items.Add(newCat);
        m.Items.Insert(3, cat);

        if (pre.DefconEnabled && Edition.Has(ProFeature.LevelLayouts))
        {
            var dc = new MenuItem { Header = "Show at DEFCON" };
            var all = new MenuItem { Header = (p.DefconLevelId.Length == 0 ? "✓ " : "") + "Every level" };
            all.Click += (_, _) => { p.DefconLevelId = ""; DataStore.NotifyNotes(); };
            dc.Items.Add(all);
            foreach (var l in pre.Defcon.Levels)
            {
                var mi = new MenuItem { Header = (p.DefconLevelId == l.Id ? "✓ " : "") + "Only " + l.Label };
                string id = l.Id;
                mi.Click += (_, _) => { p.DefconLevelId = id; DataStore.NotifyNotes(); };
                dc.Items.Add(mi);
            }
            m.Items.Insert(4, dc);
        }

        var opts = new MenuItem { Header = "Text size and spacing" };
        foreach (var size in new[] { 11.0, 12, 13, 14, 16, 18, 20 })
        {
            var mi = new MenuItem { Header = (Math.Abs(p.FontSize - size) < 0.1 ? "✓ " : "") + $"Text {size:0}" };
            double s = size;
            mi.Click += (_, _) => { p.FontSize = s; DataStore.NotifyNotes(); };
            opts.Items.Add(mi);
        }
        opts.Items.Add(new Separator());
        foreach (var sp in new[] { 1.0, 1.15, 1.25, 1.4, 1.6 })
        {
            var mi = new MenuItem { Header = (Math.Abs(p.LineSpacing - sp) < 0.01 ? "✓ " : "") + $"Line spacing {sp:0.##}" };
            double s = sp;
            mi.Click += (_, _) => { p.LineSpacing = s; DataStore.NotifyNotes(); };
            opts.Items.Add(mi);
        }
        opts.Items.Add(new Separator());
        var confirm = new MenuItem { Header = (p.ConfirmDelete ? "✓ " : "") + "Ask before deleting / clearing" };
        confirm.Click += (_, _) => { p.ConfirmDelete = !p.ConfirmDelete; DataStore.MarkDirty(); };
        opts.Items.Add(confirm);
        m.Items.Insert(5, opts);
        return m;
    }

    private void PickIcon(NotePad p)
    {
        var w = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, SizeToContent = SizeToContent.WidthAndHeight, Topmost = true, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen, Owner = HostWindow };
        var frame = new HudFrame { Padding = new Thickness(16), Margin = new Thickness(10), Width = 380, Content = Ui.Stack(Ui.H2("PAD ICON"), Ui.Spacer(8), Ui.IconPicker(p.Icon, g => { p.Icon = g; DataStore.NotifyNotes(); w.Close(); })) };
        frame.SetResourceReference(HudFrame.FillProperty, "B.Bg");
        frame.SetResourceReference(HudFrame.StrokeProperty, "B.Accent");
        w.Content = frame;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Deactivated += (_, _) => { try { w.Close(); } catch (InvalidOperationException) { } };
        w.Show();
    }

    private bool Confirm(NotePad p, string title, string message, string yes) =>
        !(p.ConfirmDelete && DataStore.Settings.ConfirmDeletes) || Ui.Confirm(HostWindow, title, message, yes);

    public void ClearPad(NotePad p)
    {
        if (p.Notes.Count == 0) return;
        if (!Confirm(p, "Clear pad", $"Remove all {p.Notes.Count} notes from \"{p.Title}\"? You can undo this from the pad toolbar.", "CLEAR")) return;
        int n = Pads.Clear(p);
        Flash($"Cleared {n} · undo brings them back");
    }

    private void DeletePad(NotePad p)
    {
        if (!Ui.Confirm(HostWindow, "Delete pad", $"Delete \"{p.Title}\" and its {p.Notes.Count} notes? A backup is made first.", "DELETE")) return;
        try { Pads.Delete(p); Refresh(); }
        catch (InvalidOperationException ex) { Ui.Info(HostWindow, "Note pads", ex.Message); }
    }

    private void ExportPad(NotePad p)
    {
        var path = Ui.SaveFile("Export note pad", Ui.SafeFileName(p.Title) + ".sfhudpad", "SFHud note pad (*.sfhudpad)|*.sfhudpad|Markdown (*.md)|*.md");
        if (path is null) return;
        try
        {
            if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) File.WriteAllText(path, Pads.PadAsText(p));
            else DataStore.ExportJson(path, "pad", p);
            Flash("Exported");
        }
        catch (Exception ex) { Ui.Info(HostWindow, "Export failed", ex.Message); }
    }

    private void ImportPad()
    {
        var path = Ui.OpenFile("Import note pad", "SFHud note pad (*.sfhudpad;*.shudpad)|*.sfhudpad;*.shudpad");
        if (path is null) return;
        try
        {
            var pad = DataStore.ImportJson<NotePad>(path, "pad") ?? throw new InvalidDataException("Empty file.");
            pad.Id = Ids.New();
            pad.DefconLevelId = "";
            pad.Title = DataStore.UniqueName(pad.Title, DataStore.Preset.Pads.Select(x => x.Title));
            DataStore.Preset.Pads.Add(pad);
            if (!DataStore.Preset.Categories.Contains(pad.Category)) DataStore.Preset.Categories.Add(pad.Category);
            DataStore.NotifyNotes();
            DataStore.NotifyContext();
            SelectPad(pad);
            Refresh();
        }
        catch (Exception ex) { Ui.Info(HostWindow, "Import failed", ex.Message); }
    }

    private void ImportDocument()
    {
        var path = Ui.OpenFile("Import a document as a pad", "Text and Markdown (*.txt;*.md)|*.txt;*.md|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 4 * 1024 * 1024) throw new InvalidDataException("That file is over 4 MB.");
            var pad = Pads.ImportDocument(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path));
            SelectPad(pad);
            Refresh();
        }
        catch (Exception ex) { Ui.Info(HostWindow, "Import failed", ex.Message); }
    }

    private void UndoDelete()
    {
        int n = Pads.Undo(Pads.Current, 1);
        Flash(n == 0 ? "Nothing to undo" : "Restored");
    }

    public void PasteClipboard()
    {
        if (!Clipboard.ContainsText()) { Flash("Clipboard has no text"); return; }
        Pads.Add(Pads.Current, Clipboard.GetText().Trim());
        Flash("Pasted as a note");
    }

    // ================================================================ notes

    private void RenderNotes()
    {
        _notes.Children.Clear();
        if (_filter.Length > 0)
        {
            var hits = Pads.Search(_filter).Take(100).ToList();
            _notes.Children.Add(Ui.Label($"{hits.Count} RESULT{(hits.Count == 1 ? "" : "S")} IN ALL PADS"));
            foreach (var h in hits) _notes.Children.Add(NoteCard(h.Pad, h.Note, showPad: true));
            return;
        }
        var pad = Pads.Current;
        var ordered = pad.Notes.Where(n => n.Pinned).Concat(pad.Notes.Where(n => !n.Pinned)).ToList();
        if (ordered.Count == 0)
        {
            _notes.Children.Add(Ui.Hint($"\"{pad.Title}\" is empty. Type below and press Ctrl+Enter. Formatting: **bold**, *italic*, __underline__, ~~strike~~, ==highlight==, # heading, - bullet, 1. list, - [ ] task."));
            return;
        }
        foreach (var n in ordered) _notes.Children.Add(NoteCard(pad, n, showPad: false));
    }

    private FrameworkElement NoteCard(NotePad pad, PadNote n, bool showPad)
    {
        var accent = ThemeManager.BrushOr(n.LabelColor.Length > 0 ? n.LabelColor : pad.Color, "B.Accent");
        var opts = new MarkupOptions
        {
            FontSize = pad.FontSize, LineSpacing = pad.LineSpacing, Foreground = ThemeManager.Brush("B.Text"), Accent = accent, Dim = ThemeManager.Brush("B.Dim"),
            FontFamily = (FontFamily)Application.Current.Resources["F.Body"], MonoFamily = (FontFamily)Application.Current.Resources["F.Mono"],
            OnToggleTask = line => Pads.ToggleTask(n, line),
            OnResizeImage = (line, w) => Pads.ResizeImage(n, line, w),
            OnRemoveImage = line => Pads.RemoveImage(n, line),
        };
        var body = NoteMarkup.Render(n.Text, opts);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        if (n.Pinned) { var pin = Ui.Icon("", 10, "B.Amber"); pin.Margin = new Thickness(0, 0, 6, 0); meta.Children.Add(pin); }
        if (n.Label.Length > 0)
        {
            var lab = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(0, 0, 6, 0), Background = ThemeManager.BrushOr(n.LabelColor, "B.Accent", 0.25) };
            var lt = Ui.Label(n.Label.ToUpperInvariant(), 9.5);
            lt.Foreground = accent;
            lab.Child = lt;
            meta.Children.Add(lab);
        }
        meta.Children.Add(Ui.Label((showPad ? pad.Title.ToUpperInvariant() + "  ·  " : "") + n.Time + (n.Modified - n.Created > TimeSpan.FromMinutes(1) ? "  ·  edited" : "") + (n.HiddenOnOverlay ? "  ·  not in game" : "") + (IsPoppedOut?.Invoke(n) == true ? "  ·  pinned on overlay" : ""), 9.5));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Opacity = 0 };
        bool poppedOut = IsPoppedOut?.Invoke(n) == true;
        if (PopOut is { } popOut)
        {
            var pop = Ui.IconButton(poppedOut ? "" : "", poppedOut ? "Take this note off the overlay" : "Pop out onto the overlay (its own movable card over the game)", () => { bool now = popOut(n); Flash(now ? "On the overlay · move it in customize mode" : "Taken off the overlay"); RenderNotes(); }, 12);
            if (poppedOut) pop.SetResourceReference(Control.ForegroundProperty, "B.Amber");
            actions.Children.Add(pop);
        }
        actions.Children.Add(Ui.IconButton("", "Edit", () => BeginEdit(pad, n), 12));
        actions.Children.Add(Ui.IconButton("", "Copy", () => { Clipboard.SetText(n.Text); Flash("Copied"); }, 12));
        actions.Children.Add(Ui.IconButton(n.Pinned ? "" : "", n.Pinned ? "Unpin" : "Pin to top", () => Pads.TogglePin(n), 12));
        actions.Children.Add(Ui.IconButton("", "More", () => NoteMenu(pad, n).IsOpen = true, 12));
        actions.Children.Add(Ui.IconButton("", "Delete", () => DeleteNote(pad, n), 12));

        var head = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(meta);

        var inner = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        inner.Children.Add(head);
        inner.Children.Add(body);

        bool marked = n.LabelColor.Length > 0 || n.Pinned;
        var markBrush = n.LabelColor.Length > 0 ? accent : ThemeManager.Brush("B.Amber");
        var card = new HudFrame
        {
            Content = inner, Padding = new Thickness(14, 9, 8, 11), Margin = new Thickness(0, 0, 0, 7), Chamfer = 9, ShowBrackets = marked, BracketLength = 12,
            ShowTab = marked, TabBrush = markBrush, BracketBrush = markBrush,
            Stroke = marked ? markBrush : ThemeManager.Brush("B.Line"),
        };
        card.SetResourceReference(HudFrame.FillProperty, "B.Panel");
        card.MouseEnter += (_, _) => actions.Opacity = 1;
        card.MouseLeave += (_, _) => actions.Opacity = 0;
        card.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { BeginEdit(pad, n); e.Handled = true; } };
        card.ContextMenu = NoteMenu(pad, n);
        return card;
    }

    private ContextMenu NoteMenu(NotePad pad, PadNote n)
    {
        var m = Ui.Menu(
            ("Edit", () => BeginEdit(pad, n)),
            ("Copy", () => Clipboard.SetText(n.Text)),
            ("Copy as plain text", () => Clipboard.SetText(NoteMarkup.PlainText(n.Text))),
            ("Duplicate", () => Pads.DuplicateNote(pad, n)),
            (n.Pinned ? "Unpin" : "Pin to top", () => Pads.TogglePin(n)),
            (n.HiddenOnOverlay ? "Show on overlay" : "Hide on overlay", () => Pads.ToggleHidden(n)),
            (PopOut is null ? null : IsPoppedOut?.Invoke(n) == true ? "Take off the overlay (pinned card)" : "Pop out as its own card on the overlay", PopOut is null ? null : () => { PopOut?.Invoke(n); RenderNotes(); }),
            ("Set label text…", () => { if (Ui.Prompt(HostWindow, "Label", n.Label, "A short tag shown above the note, e.g. ORDERS, INTEL, TODO.") is { } t) { n.Label = t.Trim(); DataStore.NotifyNotes(); } }),
            (null, null),
            ("Delete", () => DeleteNote(pad, n)));
        var colour = new MenuItem { Header = "Label colour" };
        foreach (var (name, hex) in new[] { ("None", "") }.Concat(NoteMarkup.Colors))
        {
            var mi = new MenuItem { Header = name };
            string h = hex;
            mi.Click += (_, _) => { n.LabelColor = h; DataStore.NotifyNotes(); };
            colour.Items.Add(mi);
        }
        m.Items.Insert(7, colour);
        var move = new MenuItem { Header = "Move to pad" };
        foreach (var other in DataStore.Preset.Pads.Where(x => x != pad))
        {
            var mi = new MenuItem { Header = other.Title };
            var o = other;
            mi.Click += (_, _) => Pads.MoveNote(pad, n, o);
            move.Items.Add(mi);
        }
        if (move.Items.Count > 0) m.Items.Insert(8, move);
        return m;
    }

    private void DeleteNote(NotePad pad, PadNote n)
    {
        if (!Confirm(pad, "Delete note", "Delete this note? Undo in the pad toolbar brings it back.", "DELETE")) return;
        if (_editing == n) CancelEdit();
        Pads.Remove(pad, n);
        Flash("Deleted · undo restores");
    }

    private void BeginEdit(NotePad pad, PadNote n)
    {
        if (pad.Id != Pads.Current.Id) SelectPad(pad);
        FlushDraft();
        _editing = n;
        _loading = true;
        _editor.Text = n.Text;
        _loading = false;
        _saveButton.Content = "SAVE CHANGES";
        _editorState.Text = "EDITING · ESC CANCELS";
        RenderPreview();
        FocusEditor();
    }

    private void CancelEdit()
    {
        _editing = null;
        _saveButton.Content = "ADD NOTE";
        LoadDraft();
    }

    private void Commit()
    {
        string text = _editor.Text.TrimEnd();
        if (_editing is { } n)
        {
            if (text.Length == 0) { DeleteNote(Pads.Current, n); return; }
            Pads.Edit(n, text);
            _editing = null;
            _saveButton.Content = "ADD NOTE";
            LoadDraft();
            Flash("Saved");
            return;
        }
        if (text.Trim().Length == 0) return;
        var pad = Pads.Current;
        Pads.Add(pad, text);
        pad.Draft = "";
        _loading = true;
        _editor.Text = "";
        _loading = false;
        DataStore.MarkDirty();
        Flash("Added");
        _scroll.ScrollToTop();
    }

    private void RenderPreview()
    {
        if (_previewHost.Visibility != Visibility.Visible) return;
        var pad = Pads.Current;
        _previewHost.Child = NoteMarkup.Render(_editor.Text.Length == 0 ? "*Nothing to preview*" : _editor.Text, new MarkupOptions
        {
            FontSize = pad.FontSize, LineSpacing = pad.LineSpacing, Foreground = ThemeManager.Brush("B.Text"), Accent = ThemeManager.BrushOr(pad.Color, "B.Accent"),
            Dim = ThemeManager.Brush("B.Dim"), FontFamily = (FontFamily)Application.Current.Resources["F.Body"], Interactive = false,
        });
    }
}
