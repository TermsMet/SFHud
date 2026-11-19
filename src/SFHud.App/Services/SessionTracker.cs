using System;
using SFHud.Interop;

namespace SFHud.Services;

/// <summary>
/// Counts time the target game is actually in front. The session resets when the game closes;
/// lifetime totals per game are kept in settings.
/// </summary>
public sealed class SessionTracker : Observable
{
    public static SessionTracker Instance { get; } = new();

    private double _session;
    private bool _running;
    private bool _inFront;
    private DateTime _last = DateTime.UtcNow;

    public double SessionSeconds { get => _session; private set { if (Set(ref _session, value)) Raise(nameof(SessionText)); } }
    public bool Running { get => _running; private set => Set(ref _running, value); }
    public bool InFront { get => _inFront; private set => Set(ref _inFront, value); }

    public string SessionText
    {
        get
        {
            var t = TimeSpan.FromSeconds(_session);
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
        }
    }

    public void Update(GameTarget target, AppSettings settings)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _last).TotalSeconds, 0, 5);
        _last = now;

        Running = GameLocator.IsRunning(target);
        InFront = Running && GameLocator.IsForeground(target);

        if (!Running)
        {
            SessionSeconds = 0;
            return;
        }
        if (InFront)
        {
            SessionSeconds += dt;
            settings.PlaytimeSeconds.TryGetValue(target.Id, out double total);
            settings.PlaytimeSeconds[target.Id] = total + dt;
        }
    }

    public void ResetForTargetChange()
    {
        SessionSeconds = 0;
        _last = DateTime.UtcNow;
    }
}
