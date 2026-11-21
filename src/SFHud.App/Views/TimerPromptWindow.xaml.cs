using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SFHud.Interop;
using SFHud.Services;

namespace SFHud.Views;

/// <summary>
/// Manual timer (Ctrl+Alt+T): type a length and an optional label, Enter starts it and hands focus
/// back to the game. Understands clock targets (@21:30) and repeating timers (every 10m).
/// </summary>
public partial class TimerPromptWindow : Window
{
    private static readonly string[] Chips = { "1m", "2m", "5m", "10m", "15m", "30m", "every 5m patrol", "every 10m patrol" };

    private readonly GameTarget _target;
    private bool _closing;

    public TimerPromptWindow(GameTarget target)
    {
        InitializeComponent();
        _target = target;
        ClockText.Text = $"NOW {DateTime.Now:HH:mm}";
        InputBox.Text = App.Settings.LastManualTimer;
        InputBox.SelectAll();

        foreach (var chip in Chips)
        {
            var b = new Button { Content = chip.ToUpperInvariant(), Style = (Style)FindResource("Sf.Button.Small"), Margin = new Thickness(0, 0, 6, 6), Tag = chip };
            b.Click += (_, _) => StartFrom((string)b.Tag);
            Presets.Children.Add(b);
        }

        Loaded += (_, _) => { SFHud.Interop.GameScreen.CenterOnGame(this, _target); Activate(); InputBox.Focus(); Keyboard.Focus(InputBox); UpdatePreview(); };
        Deactivated += (_, _) => SafeClose();
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text)) { PreviewText.Text = ""; return; }
        if (TimerParser.TryParse(InputBox.Text, out var r))
        {
            var ends = DateTime.Now + r.Duration;
            PreviewText.Foreground = (System.Windows.Media.Brush)FindResource("B.Accent");
            PreviewText.Text = $"{(r.Repeat ? "↻ " : "")}{TimerParser.Describe(r.Duration)} → {ends:HH:mm:ss}";
        }
        else
        {
            PreviewText.Foreground = (System.Windows.Media.Brush)FindResource("B.Alert");
            PreviewText.Text = "CAN'T READ THAT";
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (StartFrom(InputBox.Text)) return;
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SafeClose();
            GameLocator.FocusTarget(_target);
        }
    }

    private bool StartFrom(string text)
    {
        if (!TimerParser.TryParse(text, out var r)) { UpdatePreview(); return false; }
        TimerService.Instance.Start(r);
        App.Settings.LastManualTimer = text.Trim();
        SafeClose();
        GameLocator.FocusTarget(_target);
        return true;
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
