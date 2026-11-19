using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SFHud.Controls;

/// <summary>
/// TextBlock that "decodes" its text left to right through scrambled glyphs when it loads
/// or when <see cref="SourceText"/> changes. Shows the final text immediately under reduced motion.
/// </summary>
public class GlitchText : TextBlock
{
    private const string Glyphs = "!<>-_/\\[]{}=+*^?#0123456789ABCDEF";
    private static readonly Random Rng = new();

    private readonly DispatcherTimer _timer;
    private DateTime _start;

    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText), typeof(string), typeof(GlitchText),
        new PropertyMetadata(string.Empty, (d, _) => ((GlitchText)d).Play()));

    public static readonly DependencyProperty DurationMsProperty = DependencyProperty.Register(
        nameof(DurationMs), typeof(double), typeof(GlitchText), new PropertyMetadata(900.0));

    public string SourceText { get => (string)GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }
    public double DurationMs { get => (double)GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }

    public GlitchText()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => Play();
        Unloaded += (_, _) => _timer.Stop();
    }

    public void Play()
    {
        string src = SourceText ?? string.Empty;
        if (!IsLoaded || App.ReducedMotion || src.Length == 0)
        {
            _timer.Stop();
            Text = src;
            return;
        }
        _start = DateTime.UtcNow;
        _timer.Start();
        OnTick(null, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        string src = SourceText ?? string.Empty;
        double p = (DateTime.UtcNow - _start).TotalMilliseconds / Math.Max(1, DurationMs);
        if (p >= 1)
        {
            _timer.Stop();
            Text = src;
            return;
        }

        int revealed = (int)(src.Length * p);
        var sb = new StringBuilder(src.Length);
        for (int i = 0; i < src.Length; i++)
        {
            char ch = src[i];
            sb.Append(i < revealed || char.IsWhiteSpace(ch) ? ch : Glyphs[Rng.Next(Glyphs.Length)]);
        }
        Text = sb.ToString();
    }
}
