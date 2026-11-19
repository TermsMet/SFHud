using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SFHud.Services;

namespace SFHud.Controls;

/// <summary>Property editor for one overlay element (in-game customize menu and the dashboard's Overlay page).</summary>
public static class ElementEditor
{
    private static string[]? _fonts;

    public static string[] FontList => _fonts ??= new[] { "" }.Concat(System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(f => f)).Distinct().ToArray();

    /// <param name="changed">Called after any edit (rebuild = the element must be redrawn).</param>
    public static StackPanel Build(OverlayElement el, Action changed, Action? delete = null, Action? duplicate = null, Action? reset = null)
    {
        void C() { DataStore.MarkDirty(); changed(); }
        var sp = new StackPanel();
        void Restructure()
        {
            var fresh = Build(el, changed, delete, duplicate, reset);
            var kids = fresh.Children.Cast<UIElement>().ToList();
            fresh.Children.Clear();
            sp.Children.Clear();
            foreach (var k in kids) sp.Children.Add(k);
        }

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var ic = Ui.Icon(ElementCatalog.Glyph(el.Kind), 16);
        ic.Margin = new Thickness(0, 0, 8, 0);
        head.Children.Add(ic);
        head.Children.Add(Ui.H2(el.KindName.ToUpperInvariant()));
        sp.Children.Add(head);

        sp.Children.Add(Ui.Switch("Visible", el.Visible, v => { el.Visible = v; C(); }, "Hidden elements stay on the page but aren't drawn."));
        sp.Children.Add(Ui.Row("Name", Ui.TextBox(el.Name, v => { el.Name = v.Trim(); C(); }), "Shown in lists and keybinds. Doesn't appear on screen."));

        if (el.Kind is ElementKind.InfoCard or ElementKind.Timers or ElementKind.Defcon or ElementKind.Intel or ElementKind.NotePad or ElementKind.Media or ElementKind.Note or ElementKind.Video)
        {
            sp.Children.Add(Ui.Row("Title", Ui.TextBox(el.Title, v => { el.Title = v.Trim(); C(); }), "Header text. Empty uses the default."));
            sp.Children.Add(Ui.Switch("Show title", el.ShowTitle, v => { el.ShowTitle = v; C(); }));
        }
        if (el.Kind == ElementKind.InfoCard)
        {
            sp.Children.Add(Ui.Label("ICON"));
            sp.Children.Add(Ui.IconPicker(el.Icon, g => { el.Icon = g; C(); }));
        }
        if (el.Kind is ElementKind.InfoCard or ElementKind.Text)
        {
            sp.Children.Add(Ui.Label("TEXT (FORMATTED)"));
            var box = Ui.TextBox(el.Text, v => { if (v != el.Text) { el.Text = v; C(); } }, multiline: true, minHeight: 90);
            box.FontFamily = (FontFamily)Application.Current.Resources["F.Body"];
            sp.Children.Add(FormatBar.For(box, compact: true));
            sp.Children.Add(box);
        }
        if (el.Kind == ElementKind.NotePad)
        {
            var pads = DataStore.Preset.Pads;
            sp.Children.Add(Ui.Row("Note pad", Ui.Combo(pads, pads.FirstOrDefault(p => p.Id == el.PadId), p => { el.PadId = p.Id; C(); }, p => p.Title)));
            sp.Children.Add(Ui.Slider("Notes shown", 1, 12, el.MaxItems, v => $"{v:0}", v => { el.MaxItems = (int)v; C(); }, step: 1));
        }
        if (el.Kind == ElementKind.Note)
        {
            var hit = Pads.FindNote(el.NoteId);
            sp.Children.Add(Ui.Hint(hit is { } h ? $"Shows one note from \"{h.Pad.Title}\": {Clip(h.Note.FirstLine, 60)}. Edit the note in its pad and this updates. Pop out other notes with the pop-out button on any note." : "The note this showed was deleted."));
            var all = DataStore.Preset.Pads.SelectMany(p => p.Notes.Select(n => (p, n))).ToList();
            if (all.Count > 0)
                sp.Children.Add(Ui.Row("Note", Ui.Combo(all.Select(x => x.n.Id).ToList(), el.NoteId, id => { el.NoteId = id; C(); },
                    id => all.FirstOrDefault(x => x.n.Id == id) is var f && f.n is not null ? $"{f.p.Title} · {Clip(f.n.FirstLine, 40)}" : "(deleted)")));
        }
        if (el.Kind == ElementKind.Video)
        {
            var src = Ui.TextBox(el.VideoSource, v => { if (v.Trim() == el.VideoSource) return; el.VideoSource = v.Trim(); C(); });
            sp.Children.Add(Ui.Row("Video", src, "A video file on this PC, or a direct link to a video file (ends in .mp4 and similar). Page links like YouTube aren't video files; play those in the Steam overlay browser."));
            sp.Children.Add(Ui.Wrap(
                Ui.Button("PICK A FILE…", () =>
                {
                    if (Ui.OpenFile("Pick a video", VideoHub.FileFilter) is not { } f) return;
                    el.VideoSource = f;
                    src.Text = f;
                    C();
                }, "Sf.Button.Small.Primary"),
                Ui.Button("PLAY / PAUSE", () => VideoHub.All.FirstOrDefault(p => p.El == el)?.Toggle()),
                Ui.Button("RESTART", () => VideoHub.All.FirstOrDefault(p => p.El == el)?.Restart())));
            sp.Children.Add(Ui.Switch("Loop", el.VideoLoop, v => { el.VideoLoop = v; C(); }));
            sp.Children.Add(Ui.Switch("Muted", el.VideoMuted, v => { el.VideoMuted = v; C(); }, "Videos start muted so they never drown out the game."));
            sp.Children.Add(Ui.Slider("Volume", 0, 1, el.VideoVolume, v => $"{v:P0}", v => { el.VideoVolume = Math.Round(v, 2); C(); }));
            sp.Children.Add(Ui.Hint("Videos pause while you're out of the game (Settings › Music & video)."));
        }

        sp.Children.Add(Ui.Spacer(4));
        sp.Children.Add(Ui.H2("SIZE", 11));
        sp.Children.Add(Ui.Slider("Width", 80, 1200, el.W, v => $"{v:0}", v => { el.W = Math.Round(v); C(); }, "Drag the corner handles on the overlay too.", 2));
        sp.Children.Add(Ui.Switch("Fit height to content", el.H <= 0, v => { el.H = v ? 0 : 220; C(); Restructure(); }, "Off = fixed height with scrolling for long briefings."));
        if (el.H > 0) sp.Children.Add(Ui.Slider("Height", 40, 1200, el.H, v => $"{v:0}", v => { el.H = Math.Round(v); C(); }, step: 2));
        sp.Children.Add(Ui.Slider("Text size", 8, 36, el.FontSize, v => $"{v:0.#}", v => { el.FontSize = Math.Round(v * 2) / 2; C(); }, step: 0.5));
        sp.Children.Add(Ui.Row("Font", Ui.Combo(FontList, el.FontFamily, f => { el.FontFamily = f; C(); }, f => f.Length == 0 ? "Theme font" : f)));

        sp.Children.Add(Ui.Spacer(4));
        sp.Children.Add(Ui.H2("LOOK", 11));
        sp.Children.Add(Ui.Label("ACCENT"));
        sp.Children.Add(Ui.ColorPicker(el.Accent, v => { el.Accent = v; C(); }));
        sp.Children.Add(Ui.Label("TEXT COLOUR"));
        sp.Children.Add(Ui.ColorPicker(el.Foreground, v => { el.Foreground = v; C(); }));
        sp.Children.Add(Ui.Label("BACKGROUND"));
        sp.Children.Add(Ui.ColorPicker(el.Background, v => { el.Background = v; C(); }));
        sp.Children.Add(Ui.Slider("Background opacity", 0, 1, el.BackgroundOpacity, v => $"{v:P0}", v => { el.BackgroundOpacity = Math.Round(v, 2); C(); }));
        sp.Children.Add(Ui.Slider("Element opacity", 0.1, 1, el.Opacity, v => $"{v:P0}", v => { el.Opacity = Math.Round(v, 2); C(); }));
        sp.Children.Add(Ui.Row("Border", Ui.Combo(Enum.GetValues<BorderKind>(), el.Border, b => { el.Border = b; C(); }, b => b switch { BorderKind.Hud => "HUD frame", BorderKind.Line => "Thin line", BorderKind.Rounded => "Rounded", _ => "None" })));
        sp.Children.Add(Ui.Slider("Border thickness", 0, 4, el.BorderThickness, v => $"{v:0.#}", v => { el.BorderThickness = Math.Round(v, 1); C(); }, step: 0.5));
        sp.Children.Add(Ui.Slider("Corner size", 0, 24, el.CornerRadius, v => $"{v:0}", v => { el.CornerRadius = Math.Round(v); C(); }, step: 1));
        sp.Children.Add(Ui.Slider("Padding", 0, 32, el.Padding, v => $"{v:0}", v => { el.Padding = Math.Round(v); C(); }, step: 1));

        var levels = DataStore.Preset.Defcon.Levels;
        if (DataStore.Preset.DefconEnabled && levels.Count > 0)
        {
            sp.Children.Add(Ui.Spacer(4));
            sp.Children.Add(Ui.H2("SHOW AT DEFCON", 11));
            sp.Children.Add(Ui.Hint("None selected = always shown. Pick levels to make this a DEFCON-specific element."));
            var wrap = new WrapPanel();
            foreach (var l in levels)
            {
                var chip = new System.Windows.Controls.Primitives.ToggleButton { Content = l.Short + " · " + l.Name, Style = Ui.Style("Sf.Chip"), IsChecked = el.DefconFilter.Contains(l.Id), ToolTip = l.Label };
                string id = l.Id;
                chip.Click += (_, _) => { if (chip.IsChecked == true) el.DefconFilter.Add(id); else el.DefconFilter.Remove(id); C(); };
                wrap.Children.Add(chip);
            }
            sp.Children.Add(wrap);
        }

        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        if (duplicate is not null) actions.Children.Add(Ui.Button("DUPLICATE", duplicate));
        if (reset is not null) actions.Children.Add(Ui.Button("RESET LOOK", reset, tip: "Back to the default size and colours for this element type (keeps position and text)."));
        if (delete is not null) actions.Children.Add(Ui.Button("REMOVE", delete, "Sf.Button.Small.Danger"));
        sp.Children.Add(actions);
        return sp;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Restores default look for the element's kind while keeping where it is and what it says.</summary>
    public static void ResetLook(OverlayElement el)
    {
        var d = ElementCatalog.Create(el.Kind, el.X, el.Y);
        el.W = d.W; el.H = d.H; el.FontSize = d.FontSize; el.FontFamily = ""; el.Accent = el.Foreground = el.Background = "";
        el.BackgroundOpacity = d.BackgroundOpacity; el.Opacity = 1; el.Border = d.Border; el.BorderThickness = 1; el.CornerRadius = 8; el.Padding = d.Padding;
        el.ShowTitle = true;
    }
}
