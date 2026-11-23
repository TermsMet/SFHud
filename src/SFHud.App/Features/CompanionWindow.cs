using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SFHud.Controls;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>
/// The active overlay page as a normal window (for exclusive-fullscreen games or a second monitor).
/// Elements are interactive here: tick tasks, accept DEFCON suggestions, play / pause music.
/// </summary>
public sealed class CompanionWindow : Window
{
    private readonly StackPanel _list = new();
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private bool _queued;

    public CompanionWindow()
    {
        Title = "SFHud Companion";
        Width = 380;
        Height = 720;
        MinWidth = 280;
        MinHeight = 300;
        SetResourceReference(BackgroundProperty, "B.Bg");
        SetResourceReference(ForegroundProperty, "B.Text");
        SetResourceReference(FontFamilyProperty, "F.Body");
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/SFHud;component/Assets/sfhud.ico")); } catch (Exception) { }

        var head = new DockPanel { Margin = new Thickness(14, 12, 14, 8) };
        var pages = new ComboBox { MinWidth = 150 };
        foreach (var p in DataStore.Preset.Pages) pages.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        pages.SelectedIndex = DataStore.Preset.Pages.IndexOf(DataStore.Preset.ActivePage);
        pages.SelectionChanged += (_, _) => { if (pages.SelectedItem is ComboBoxItem { Tag: string id }) { DataStore.Preset.ActivePageId = id; DataStore.MarkDirty(); Rebuild(); } };
        DockPanel.SetDock(pages, Dock.Right);
        head.Children.Add(pages);
        head.Children.Add(Ui.H2("COMPANION"));
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14, 0, 10, 14) });
        Content = dock;

        _tick.Tick += (_, _) => { foreach (var v in _list.Children.OfType<ElementView>()) v.Tick(); };
        DataStore.NotesChanged += Soon;
        DataStore.ContextChanged += Soon;
        ThemeManager.Changed += Soon;
        DefconService.Instance.PropertyChanged += (_, _) => Soon();
        MediaService.Instance.Changed += Soon;
        TimerService.Instance.Timers.CollectionChanged += (_, _) => Soon();
        IsVisibleChanged += (_, _) => { if (IsVisible) { _tick.Start(); Rebuild(); } else _tick.Stop(); };
        Closing += (_, e) => { e.Cancel = true; Hide(); }; // keep the instance; SFHud closes it on exit
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    private void Soon()
    {
        if (!IsVisible || _queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(() => { _queued = false; Rebuild(); }, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        _list.Children.Clear();
        var ctx = new ElementContext { Interactive = true, TargetName = () => GameCatalog.Get(DataStore.Settings).Name, SessionText = () => SessionTracker.Instance.SessionText, GameAttached = () => SessionTracker.Instance.Running };
        foreach (var el in DataStore.Preset.ActivePage.Elements.Where(e => e.Visible))
        {
            var v = new ElementView(el, ctx) { Margin = new Thickness(0, 0, 0, 10), HorizontalAlignment = HorizontalAlignment.Stretch };
            v.Width = double.NaN;
            _list.Children.Add(v);
        }
        if (_list.Children.Count == 0) _list.Children.Add(Ui.Hint("This page has no visible elements."));
    }

}
