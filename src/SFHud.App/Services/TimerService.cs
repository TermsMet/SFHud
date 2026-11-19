using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;

namespace SFHud.Services;

public sealed class QuickTimer : Observable
{
    private string _remaining = "";
    private bool _urgent;
    private bool _done;
    private double _progress = 1;
    private int _round = 1;
    private bool _paused;
    private TimeSpan _elapsedBeforePause;
    private DateTime _startedAt = DateTime.Now;

    public required string Label { get; init; }
    public required DateTime EndsAt { get; set; }
    public required TimeSpan Duration { get; init; }
    public TimerMode Mode { get; init; } = TimerMode.Countdown;
    /// <summary>Restarts itself when it runs out (patrol rotations, wave timers).</summary>
    public bool Repeat { get; init; }
    public string Color { get; init; } = "";
    public string Sound { get; init; } = "";
    public DateTime? DoneAt { get; private set; }

    public string Remaining { get => _remaining; private set => Set(ref _remaining, value); }
    public bool Urgent { get => _urgent; private set => Set(ref _urgent, value); }
    public bool Done { get => _done; private set => Set(ref _done, value); }
    public bool Paused { get => _paused; private set { if (Set(ref _paused, value)) Raise(nameof(PauseGlyph)); } }
    public string PauseGlyph => Paused ? "" : "";
    /// <summary>1 → 0 as the timer runs down (stopwatches stay at 1).</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public int Round { get => _round; private set { if (Set(ref _round, value)) Raise(nameof(Title)); } }
    public bool IsStopwatch => Mode == TimerMode.Stopwatch;

    public string Title => Repeat ? $"{Label}  ·  ↻ {Round}" : IsStopwatch ? $"⏱ {Label}" : Label;

    public Brush ColorBrush => ThemeManager.BrushOr(Color, "B.Accent");

    public void TogglePause(DateTime now)
    {
        if (Done) return;
        if (!Paused)
        {
            _elapsedBeforePause = IsStopwatch ? _elapsedBeforePause + (now - _startedAt) : EndsAt - now; // countdown: keep what's left
            Paused = true;
        }
        else
        {
            if (IsStopwatch) _startedAt = now;
            else EndsAt = now + _elapsedBeforePause;
            Paused = false;
        }
        Update(now);
    }

    /// <summary>Returns true on the tick the timer finishes (or completes a round).</summary>
    public bool Update(DateTime now)
    {
        if (Done) return false;
        if (IsStopwatch)
        {
            var e = Paused ? _elapsedBeforePause : _elapsedBeforePause + (now - _startedAt);
            Remaining = e.TotalHours >= 1 ? e.ToString(@"h\:mm\:ss") : e.ToString(@"mm\:ss");
            Progress = 1;
            return false;
        }
        if (Paused) return false;
        var left = EndsAt - now;
        if (left <= TimeSpan.Zero)
        {
            if (Repeat)
            {
                EndsAt += Duration;
                if (EndsAt <= now) EndsAt = now + Duration; // slept through several rounds
                Round++;
                Update(now);
                return true;
            }
            Remaining = "DONE";
            Progress = 0;
            Urgent = true;
            Done = true;
            DoneAt = now;
            return true;
        }
        Remaining = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
        Progress = Math.Clamp(left.TotalSeconds / Duration.TotalSeconds, 0, 1);
        Urgent = left.TotalSeconds <= 30;
        return false;
    }
}

/// <summary>
/// Countdown timers and stopwatches, shared by the dashboard and the overlay. Several can run at
/// once. Finished timers linger for a few seconds, then clear.
/// </summary>
public sealed class TimerService
{
    public static TimerService Instance { get; } = new();

    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };

    public ObservableCollection<QuickTimer> Timers { get; } = new();

    public event Action<QuickTimer>? Finished;

    private TimerService() => _tick.Tick += (_, _) => Update();

    public QuickTimer Start(TimeSpan duration, string? label = null, bool repeat = false, string color = "", string sound = "", TimerMode mode = TimerMode.Countdown)
    {
        if (Timers.Count >= 20) Timers.RemoveAt(0);
        var t = new QuickTimer
        {
            Label = string.IsNullOrWhiteSpace(label) ? (mode == TimerMode.Stopwatch ? "STOPWATCH" : TimerParser.Describe(duration)) : label.Trim().ToUpperInvariant(),
            Duration = mode == TimerMode.Stopwatch ? TimeSpan.FromHours(24) : duration,
            EndsAt = DateTime.Now + duration,
            Repeat = repeat && mode == TimerMode.Countdown,
            Color = color,
            Sound = sound,
            Mode = mode,
        };
        t.Update(DateTime.Now);
        Timers.Add(t);
        _tick.Start();
        return t;
    }

    public QuickTimer Start(TimerRequest r) => Start(r.Duration, r.Label, r.Repeat);

    public QuickTimer Start(TimerPreset p) => Start(TimeSpan.FromSeconds(Math.Max(1, p.Seconds)), p.Name, p.Repeat, p.Color, p.Sound, p.Mode);

    public QuickTimer StartStopwatch(string? label = null) => Start(TimeSpan.Zero, label, mode: TimerMode.Stopwatch);

    public void Cancel(QuickTimer t)
    {
        Timers.Remove(t);
        if (Timers.Count == 0) _tick.Stop();
    }

    public void CancelAll()
    {
        Timers.Clear();
        _tick.Stop();
    }

    private void Update()
    {
        var now = DateTime.Now;
        foreach (var t in Timers.ToList())
        {
            if (t.Update(now))
            {
                SoundService.Play(t.Sound.Length > 0 ? t.Sound : DataStore.Settings.TimerSound);
                Finished?.Invoke(t);
            }
            if (t.DoneAt is { } d && (now - d).TotalSeconds > 8) Timers.Remove(t);
        }
        if (Timers.Count == 0) _tick.Stop();
    }
}

public sealed record TimerRequest(TimeSpan Duration, string? Label, bool Repeat);

/// <summary>
/// Reads what people actually type into the manual timer box:
/// "5", "90s", "7m30s patrol", "1:30 breach", "1h", "@21:30 event", "every 10m patrol".
/// </summary>
public static partial class TimerParser
{
    [GeneratedRegex(@"^@\s*(\d{1,2})[:.](\d{2})\s*(am|pm)?", RegexOptions.IgnoreCase)]
    private static partial Regex ClockRx();

    [GeneratedRegex(@"^(\d{1,3}):(\d{1,2})(?::(\d{1,2}))?(?!\d)")]
    private static partial Regex ColonRx();

    [GeneratedRegex(@"^(?:(\d+(?:\.\d+)?)\s*(h|hr|hrs|hours?|m|min|mins|minutes?|s|sec|secs|seconds?)(?![a-z])\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex UnitsRx();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(h|hr|hrs|hours?|m|min|mins|minutes?|s|sec|secs|seconds?)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex UnitPartRx();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(?![\d:])")]
    private static partial Regex BareRx();

    public static bool TryParse(string input, out TimerRequest request) => TryParse(input, DateTime.Now, out request);

    public static bool TryParse(string input, DateTime now, out TimerRequest request)
    {
        request = new TimerRequest(TimeSpan.Zero, null, false);
        string s = input.Trim();
        bool repeat = false;
        foreach (var prefix in new[] { "every ", "loop ", "repeat " })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { repeat = true; s = s[prefix.Length..].TrimStart(); break; }
        }
        if (s.EndsWith(" loop", StringComparison.OrdinalIgnoreCase)) { repeat = true; s = s[..^5].TrimEnd(); }

        TimeSpan d;
        string rest;
        Match m;
        if ((m = ClockRx().Match(s)).Success)
        {
            int h = int.Parse(m.Groups[1].Value), min = int.Parse(m.Groups[2].Value);
            string ampm = m.Groups[3].Value.ToLowerInvariant();
            if (ampm == "pm" && h < 12) h += 12;
            if (ampm == "am" && h == 12) h = 0;
            if (h > 23 || min > 59) return false;
            var at = now.Date.AddHours(h).AddMinutes(min);
            if (at <= now) at = at.AddDays(1);
            d = at - now;
            repeat = false; // "every @21:30" makes no sense
            rest = s[m.Length..];
        }
        else if ((m = ColonRx().Match(s)).Success)
        {
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            d = m.Groups[3].Success
                ? new TimeSpan(a, b, int.Parse(m.Groups[3].Value))   // h:mm:ss
                : TimeSpan.FromSeconds(a * 60 + b);                  // m:ss
            rest = s[m.Length..];
        }
        else if ((m = UnitsRx().Match(s)).Success)
        {
            d = TimeSpan.Zero;
            foreach (Match part in UnitPartRx().Matches(m.Value))
            {
                double v = double.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture);
                char unit = char.ToLowerInvariant(part.Groups[2].Value[0]);
                d += unit switch { 'h' => TimeSpan.FromHours(v), 's' => TimeSpan.FromSeconds(v), _ => TimeSpan.FromMinutes(v) };
            }
            rest = s[m.Length..];
        }
        else if ((m = BareRx().Match(s)).Success)
        {
            d = TimeSpan.FromMinutes(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            rest = s[m.Length..];
        }
        else return false;

        if (d < TimeSpan.FromSeconds(1) || d > TimeSpan.FromHours(24)) return false;
        string label = rest.Trim().TrimStart('-', '·', ':').Trim();
        request = new TimerRequest(d, label.Length == 0 ? null : label, repeat);
        return true;
    }

    public static string Describe(TimeSpan d)
    {
        if (d.TotalHours >= 1) return d.Minutes == 0 ? $"{(int)d.TotalHours} H" : $"{(int)d.TotalHours} H {d.Minutes} MIN";
        if (d.TotalMinutes >= 1) return d.Seconds == 0 ? $"{(int)d.TotalMinutes} MIN" : $"{(int)d.TotalMinutes}:{d.Seconds:00}";
        return $"{d.Seconds} SEC";
    }
}
