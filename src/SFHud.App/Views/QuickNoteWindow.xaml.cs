using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SFHud.Controls;
using SFHud.Interop;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>
/// One focused field over the game. Enter saves a note into the chosen pad and hands focus back;
/// Shift+Enter adds a line; Tab switches pad; Esc or clicking away closes and keeps the draft.
/// </summary>
public partial class QuickNoteWindow : Window
{
    private readonly GameTarget _target;
    private NotePad _pad;
    private bool _closing;

    public QuickNoteWindow(GameTarget target, NotePad? pad = null)
    {
        InitializeComponent();
        _target = target;
        _pad = pad ?? Pads.Current;
        ToolbarHost.Content = FormatBar.For(NoteBox, compact: true);
        ContextText.Text = $"{target.Name.ToUpperInvariant()}  ·  {DateTime.Now:HH:mm}";
        NoteBox.Text = _pad.Draft;
        NoteBox.CaretIndex = NoteBox.Text.Length;
        DraftText.Text = NoteBox.Text.Length > 0 ? "DRAFT RESTORED" : "";
        BuildChips();

        Loaded += (_, _) => { Activate(); NoteBox.Focus(); Keyboard.Focus(NoteBox); };
        Deactivated += (_, _) => SafeClose();
    }

    private void BuildChips()
    {
        PadChips.Children.Clear();
        foreach (var p in Pads.Visible().Take(10))
        {
            var chip = new ToggleButton { Content = p.Title.ToUpperInvariant(), Style = Ui.Style("Sf.Chip"), IsChecked = p.Id == _pad.Id, ToolTip = $"Save into \"{p.Title}\"" };
            var pp = p;
            chip.Click += (_, _) => { SwitchTo(pp); NoteBox.Focus(); };
            PadChips.Children.Add(chip);
        }
        TitleText.Text = "NOTE → " + _pad.Title.ToUpperInvariant();
    }

    private void SwitchTo(NotePad p)
    {
        _pad.Draft = NoteBox.Text;
        _pad = p;
        NoteBox.Text = p.Draft;
        NoteBox.CaretIndex = NoteBox.Text.Length;
        BuildChips();
    }

    private void NoteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            string text = NoteBox.Text.Trim();
            if (text.Length > 0)
            {
                Pads.Add(_pad, text);
                _pad.Draft = "";
                DataStore.MarkDirty();
            }
            SafeClose();
            GameLocator.FocusTarget(_target);
        }
        else if (e.Key == Key.Tab)
        {
            e.Handled = true;
            var vis = Pads.Visible().ToList();
            int i = vis.IndexOf(_pad);
            SwitchTo(vis[(i + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1) + vis.Count) % vis.Count]);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SafeClose();
            GameLocator.FocusTarget(_target);
        }
    }

    private void NoteBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _pad.Draft = NoteBox.Text;
        DataStore.MarkDirty();
        DraftText.Text = NoteBox.Text.Length > 0 ? "DRAFT AUTOSAVED" : "";
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
