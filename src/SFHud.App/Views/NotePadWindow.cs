using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using SFHud.Controls;
using SFHud.Interop;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>
/// Floating, resizable note pad window that works over a borderless game. Each pad remembers its own
/// position, size, opacity, blur and always-on-top setting. Esc closes and hands focus back to the game.
/// </summary>
public sealed class NotePadWindow : Window
{
    private readonly NotePadView _view = new();
    private readonly Border _bg = new() { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly GameTarget _target;
    private NotePad _pad;
    private bool _applying;

    public NotePadWindow(GameTarget target)
    {
        _target = target;
        _pad = Pads.Current;
        Title = "SFHud Note Pads";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResize;
        SetResourceReference(FontFamilyProperty, "F.Body");
        SetResourceReference(ForegroundProperty, "B.Text");
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(7), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(10), UseAeroCaptionButtons = false });

        _view.HostWindow = this;
        _view.PadSelected += p => { SaveGeometry(); _pad = p; ApplyGeometry(); ApplyLook(); };

        // Title bar
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), Background = Brushes.Transparent };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Ui.IconButton("", "Always on top", () => { _pad.Topmost = !_pad.Topmost; ApplyLook(); DataStore.MarkDirty(); }));
        buttons.Children.Add(Ui.IconButton("", "Look: opacity, blur, size limits", () => LookMenu().IsOpen = true));
        buttons.Children.Add(Ui.IconButton("", "Close (Esc)", CloseToGame));
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        var title = Ui.H2("NOTE PADS", 12);
        title.VerticalAlignment = VerticalAlignment.Center;
        bar.Children.Add(title);
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1) { try { DragMove(); } catch (InvalidOperationException) { } SaveGeometry(); } };

        var dock = new DockPanel { Margin = new Thickness(14, 10, 12, 12) };
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_view);
        _bg.Child = dock;
        _bg.SetResourceReference(Border.BorderBrushProperty, "B.Line.Strong");
        Content = new Grid { Children = { _bg, new Border { Width = 14, Height = 14, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 3, 3), Child = Ui.Icon("", 9, "B.Muted"), IsHitTestVisible = false } } };

        SourceInitialized += (_, _) => ApplyLook();
        Loaded += (_, _) => { ApplyGeometry(); Activate(); _view.FocusEditor(); };
        SizeChanged += (_, _) => { if (IsLoaded) SaveGeometry(); };
        LocationChanged += (_, _) => { if (IsLoaded) SaveGeometry(); };
        PreviewKeyDown += (_, e) =>
        {
            // Esc always closes (what you were typing is kept as the pad's draft); Ctrl+W too.
            if (e.Key == Key.Escape || (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)) { CloseToGame(); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _view.FocusSearch(); e.Handled = true; }
            else if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Pads.Step(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); _view.SelectPad(Pads.Current); e.Handled = true; }
        };
        Closing += (_, _) => { _view.FlushDraft(); SaveGeometry(); DataStore.SaveNow(); };
    }

    public void ShowPad(NotePad pad, bool focusEditor = true)
    {
        if (pad.Id != Pads.Current.Id) _view.SelectPad(pad);
        else { _pad = pad; ApplyGeometry(); ApplyLook(); }
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (focusEditor) _view.FocusEditor();
    }

    public void ClearCurrent() => _view.ClearPad(Pads.Current);

    /// <summary>The pad currently shown (for toggle keys).</summary>
    public NotePad CurrentPad => _pad;

    private bool _closing;

    public void CloseToGame()
    {
        if (_closing) return;
        _closing = true;
        Close();
        GameLocator.FocusTarget(_target);
    }

    private ContextMenu LookMenu()
    {
        var m = new ContextMenu();
        var panel = Ui.Stack(
            Ui.Slider("Opacity", 0.3, 1, _pad.Opacity, v => $"{v:P0}", v => { _pad.Opacity = Math.Round(v, 2); ApplyLook(); DataStore.MarkDirty(); }),
            Ui.Switch("Blur behind", _pad.Blur, v => { _pad.Blur = v; ApplyLook(); DataStore.MarkDirty(); }, "Blurs what's behind the window (Windows 10 / 11)."),
            Ui.Switch("Always on top", _pad.Topmost, v => { _pad.Topmost = v; ApplyLook(); DataStore.MarkDirty(); }),
            Ui.Slider("Min width", 240, 800, _pad.MinW, v => $"{v:0}", v => { _pad.MinW = Math.Round(v); ApplyLimits(); DataStore.MarkDirty(); }, step: 10),
            Ui.Slider("Min height", 160, 800, _pad.MinH, v => $"{v:0}", v => { _pad.MinH = Math.Round(v); ApplyLimits(); DataStore.MarkDirty(); }, step: 10),
            Ui.Slider("Max width", 400, 3000, _pad.MaxW, v => $"{v:0}", v => { _pad.MaxW = Math.Round(v); ApplyLimits(); DataStore.MarkDirty(); }, step: 20),
            Ui.Slider("Max height", 300, 2400, _pad.MaxH, v => $"{v:0}", v => { _pad.MaxH = Math.Round(v); ApplyLimits(); DataStore.MarkDirty(); }, step: 20),
            Ui.Button("RESET POSITION", () => { _pad.WinX = _pad.WinY = double.NaN; _pad.WinW = 440; _pad.WinH = 520; ApplyGeometry(); DataStore.MarkDirty(); }));
        panel.Width = 260;
        panel.Margin = new Thickness(8);
        m.Items.Add(new MenuItem { Header = panel, StaysOpenOnClick = true });
        return m;
    }

    private void ApplyLimits()
    {
        MinWidth = Math.Max(240, _pad.MinW);
        MinHeight = Math.Max(160, _pad.MinH);
        MaxWidth = Math.Max(MinWidth, _pad.MaxW);
        MaxHeight = Math.Max(MinHeight, _pad.MaxH);
    }

    private void ApplyGeometry()
    {
        _applying = true;
        ApplyLimits();
        var wa = SystemParameters.WorkArea;
        Width = Math.Clamp(_pad.WinW, MinWidth, Math.Min(MaxWidth, wa.Width));
        Height = Math.Clamp(_pad.WinH, MinHeight, Math.Min(MaxHeight, wa.Height));
        // Saved as fractions of the work area so a resolution change keeps it on screen.
        double x = double.IsNaN(_pad.WinX) ? wa.Right - Width - 40 : wa.Left + _pad.WinX * Math.Max(0, wa.Width - Width);
        double y = double.IsNaN(_pad.WinY) ? wa.Top + 90 : wa.Top + _pad.WinY * Math.Max(0, wa.Height - Height);
        Left = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        Top = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        _applying = false;
    }

    private void SaveGeometry()
    {
        if (_applying || !IsLoaded || WindowState != WindowState.Normal) return;
        var wa = SystemParameters.WorkArea;
        double w = Math.Round(ActualWidth), h = Math.Round(ActualHeight);
        double x = Math.Round(Math.Clamp((Left - wa.Left) / Math.Max(1, wa.Width - ActualWidth), 0, 1), 4);
        double y = Math.Round(Math.Clamp((Top - wa.Top) / Math.Max(1, wa.Height - ActualHeight), 0, 1), 4);
        if (w == _pad.WinW && h == _pad.WinH && x == _pad.WinX && y == _pad.WinY) return;
        _pad.WinW = w; _pad.WinH = h; _pad.WinX = x; _pad.WinY = y;
        DataStore.MarkDirtyQuiet(); // saved after you stop dragging (and on close), no indicator flicker
    }

    private void ApplyLook()
    {
        Topmost = _pad.Topmost;
        var bg = ThemeManager.Brush("B.Bg") as SolidColorBrush;
        var c = bg?.Color ?? Colors.Black;
        double a = Math.Clamp(_pad.Opacity, 0.3, 1);
        // With blur on, a lighter tint still reads well.
        _bg.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * (_pad.Blur ? a * 0.82 : a)), c.R, c.G, c.B));
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) NativeMethods.SetBlur(hwnd, _pad.Blur);
    }
}
