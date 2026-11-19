using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using SFHud.Interop;

namespace SFHud.Services;

public sealed record ActionDef(string Id, string Name, string Category, string DefaultGesture = "", bool DefaultInGame = false);

/// <summary>One row of the keybind editor.</summary>
public sealed class BindRow : Observable
{
    private string _gesture = "";
    private bool _inGame;
    private string _status = "";
    private bool _problem;

    public required ActionDef Def { get; init; }
    public string Name => Def.Name;
    public string Category => Def.Category;
    public string Gesture { get => _gesture; set { if (Set(ref _gesture, value)) Raise(nameof(GestureText)); } }
    public string GestureText => Gesture.Length == 0 ? "—" : Gesture;
    public bool InGameOnly { get => _inGame; set => Set(ref _inGame, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public bool Problem { get => _problem; set => Set(ref _problem, value); }
    public bool IsDefault { get; set; }
}

/// <summary>
/// Every action SFHud can do from the keyboard. Bindings are saved per profile; anything not saved
/// uses its default. Duplicate combos and combos owned by other apps are reported, not silently dropped.
/// </summary>
public static class Keybinds
{
    public static readonly ActionDef[] Static =
    {
        new("overlay.toggle", "Show / hide the overlay", "Overlay", "Ctrl+Alt+H"),
        new("overlay.customize", "Customize overlay (in-game menu)", "Overlay", "Alt+~"),
        new("overlay.settings", "Open overlay settings (in-game)", "Overlay"),
        new("overlay.panic", "Hide all of SFHud instantly", "Overlay", "Ctrl+Alt+Backspace"),
        new("overlay.distraction", "Distraction-free mode (crosshair + toasts only)", "Overlay", "Ctrl+Alt+D"),
        new("overlay.cards", "Show / hide info cards", "Overlay", "Ctrl+Alt+P"),
        new("overlay.page.next", "Next overlay page", "Overlay", "Ctrl+Alt+]"),
        new("overlay.page.prev", "Previous overlay page", "Overlay", "Ctrl+Alt+["),
        new("overlay.crosshair", "Open crosshair editor", "Overlay", "Ctrl+Alt+X"),
        new("overlay.crosshair.toggle", "Crosshair on / off", "Overlay"),
        new("overlay.reset", "Reset overlay positions (current page)", "Overlay"),
        new("dashboard.open", "Open the SFHud window", "General", "Ctrl+Alt+K"),
        new("note.quick", "Quick note (current pad)", "Notes", "Ctrl+Alt+N"),
        new("pad.window", "Open / close note pads", "Notes", "Ctrl+Alt+J"),
        new("pad.next", "Next note pad tab", "Notes", "Ctrl+Alt+."),
        new("pad.prev", "Previous note pad tab", "Notes", "Ctrl+Alt+,"),
        new("pad.clear", "Clear notes on the current tab", "Notes"),
        new("note.paste", "Quick-paste clipboard into the current pad", "Notes", "Ctrl+Alt+V"),
        new("timer.quick", "Quick timer (default length)", "Timers", "Ctrl+T", DefaultInGame: true),
        new("timer.manual", "Set a timer (type length + label)", "Timers", "Ctrl+Alt+T"),
        new("timer.stopwatch", "Start a stopwatch", "Timers"),
        new("timers.toggle", "Show / hide timers", "Timers"),
        new("timers.clear", "Stop all timers", "Timers"),
        new("defcon.up", "DEFCON up (more alert)", "DEFCON", "Ctrl+Alt+PageUp"),
        new("defcon.down", "DEFCON down (calmer)", "DEFCON", "Ctrl+Alt+PageDown"),
        new("defcon.accept", "Accept suggested DEFCON", "DEFCON", "Ctrl+Alt+Y"),
        new("defcon.dismiss", "Dismiss suggested DEFCON", "DEFCON"),
        new("defcon.scan", "Scan for DEFCON now (screen / AI)", "DEFCON"),
        new("defcon.default", "Back to default DEFCON (undo auto-detect)", "DEFCON", "Ctrl+Alt+End"),
        new("ai.open", "Open the Local AI menu (in-game)", "AI", "Ctrl+Alt+A"),
        new("media.playpause", "Music: play / pause (your chosen source)", "Music", "Ctrl+Alt+Space"),
        new("media.next", "Music: next track", "Music"),
        new("media.prev", "Music: previous track", "Music"),
        new("media.toggleui", "Show / hide the music player", "Music", "Ctrl+Alt+M"),
        new("media.popup", "Pop-up player (music + video controls, anywhere)", "Music", "Ctrl+Alt+Shift+M"),
        new("video.playpause", "Video: play / pause", "Music"),
    };

    /// <summary>Actions that depend on the active preset (one per pad, page, DEFCON level, element and timer preset).</summary>
    public static IEnumerable<ActionDef> Dynamic(UserProfile prof)
    {
        var pre = prof.ActivePreset;
        foreach (var pad in pre.Pads)
        {
            yield return new($"pad.open.{pad.Id}", $"Open / close pad: {pad.Title}", "Note pads");
            yield return new($"pad.add.{pad.Id}", $"Add note to: {pad.Title}", "Note pads");
        }
        foreach (var page in pre.Pages) yield return new($"page.{page.Id}", $"Show page: {page.Name}", "Overlay pages");
        if (pre.DefconEnabled)
            foreach (var l in pre.Defcon.Levels) yield return new($"defcon.{l.Id}", $"Set {l.Label}", "DEFCON levels");
        foreach (var page in pre.Pages)
            foreach (var e in page.Elements)
                yield return new($"element.{e.Id}", $"Toggle: {page.Name} › {e.DisplayName}", "Overlay elements");
        foreach (var t in prof.TimerPresets) yield return new($"timer.{t.Id}", $"Start timer: {t.Name}", "Timer presets");
    }

    public static IEnumerable<ActionDef> All(UserProfile prof) => Static.Concat(Dynamic(prof));

    public static (string Gesture, bool InGame, bool IsDefault) Effective(UserProfile prof, ActionDef def)
    {
        var saved = prof.Keybinds.FirstOrDefault(k => k.Action == def.Id);
        return saved is null ? (def.DefaultGesture, def.DefaultInGame, true) : (saved.Gesture, saved.InGameOnly, false);
    }

    public static void Set(UserProfile prof, string action, string gesture, bool inGame)
    {
        prof.Keybinds.RemoveAll(k => k.Action == action);
        prof.Keybinds.Add(new KeyBind { Action = action, Gesture = Gesture.Normalize(gesture), InGameOnly = inGame });
        DataStore.MarkDirty();
    }

    public static void Reset(UserProfile prof, string action)
    {
        prof.Keybinds.RemoveAll(k => k.Action == action);
        DataStore.MarkDirty();
    }

    public static void ResetAll(UserProfile prof)
    {
        prof.Keybinds.Clear();
        DataStore.MarkDirty();
    }

    /// <summary>Binds that would clash with <paramref name="gesture"/> (for a warning before saving).</summary>
    public static IEnumerable<ActionDef> ConflictsWith(UserProfile prof, string action, string gesture)
    {
        string n = Gesture.Normalize(gesture);
        if (n.Length == 0) yield break;
        foreach (var d in All(prof))
            if (d.Id != action && Gesture.Normalize(Effective(prof, d).Gesture) == n) yield return d;
    }

    /// <summary>Warns about combos that are a bad idea (single keys steal input from the game).</summary>
    public static string? Advice(string gesture)
    {
        if (!Gesture.TryParse(gesture, out var mods, out var key)) return null;
        if (mods == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24)) return "A key without Ctrl/Alt is taken from every app, including the game. Prefer a combo.";
        if (mods == ModifierKeys.Shift) return "Shift+key blocks typing that character everywhere. Prefer Ctrl or Alt.";
        if (mods == ModifierKeys.Control && !(key is >= Key.F1 and <= Key.F24)) return "Plain Ctrl+key combos are used by many apps. Tick \"in-game only\" to keep them free elsewhere.";
        return null;
    }
}

/// <summary>Arms the profile's bindings with Windows and keeps each row's status up to date.</summary>
public sealed class KeybindHost
{
    private readonly HotkeyManager _hk;
    private readonly Action<string> _run;
    private readonly Dictionary<string, int> _ids = new();
    private readonly List<(BindRow Row, ModifierKeys Mods, Key Key)> _scoped = new();
    private bool _scopedArmed;
    private bool _suspended;

    public List<BindRow> Rows { get; } = new();
    public event Action? Updated;

    public KeybindHost(HotkeyManager hk, Action<string> run)
    {
        _hk = hk;
        _run = run;
    }

    public int ArmedCount => Rows.Count(r => r.Status is "Ready" or "In-game");
    public int ProblemCount => Rows.Count(r => r.Problem);

    /// <summary>Re-reads every binding and registers them again (after edits, profile or preset switches).</summary>
    public void Rebuild()
    {
        _hk.UnregisterAll();
        _ids.Clear();
        _scoped.Clear();
        Rows.Clear();
        var prof = DataStore.Profile;
        var seen = new Dictionary<string, BindRow>();

        foreach (var def in Keybinds.All(prof))
        {
            var (g, inGame, isDefault) = Keybinds.Effective(prof, def);
            var row = new BindRow { Def = def, Gesture = Gesture.Normalize(g), InGameOnly = inGame, IsDefault = isDefault };
            Rows.Add(row);
            if (row.Gesture.Length == 0) { row.Status = "Unbound"; continue; }
            if (seen.TryGetValue(row.Gesture, out var first))
            {
                row.Status = $"Duplicate of \"{first.Name}\"";
                row.Problem = true;
                first.Problem = true;
                if (!first.Status.StartsWith("Ready") && !first.Status.StartsWith("In-game")) continue;
                first.Status += " · also on another action";
                continue;
            }
            seen[row.Gesture] = row;
            if (_suspended) { row.Status = "Paused"; continue; }
            Gesture.TryParse(row.Gesture, out var mods, out var key);
            if (row.InGameOnly)
            {
                _scoped.Add((row, mods, key));
                row.Status = "In-game";
                continue;
            }
            Arm(row, mods, key);
        }
        if (_scopedArmed && !_suspended) foreach (var (r, m, k) in _scoped) Arm(r, m, k);
        Updated?.Invoke();
    }

    private void Arm(BindRow row, ModifierKeys mods, Key key)
    {
        string action = row.Def.Id;
        int id = _hk.Register(mods, key, () => _run(action));
        if (id < 0)
        {
            row.Status = "Taken by another app";
            row.Problem = true;
            return;
        }
        _ids[action] = id;
        row.Status = row.InGameOnly ? "In-game" : "Ready";
    }

    /// <summary>In-game-only combos are registered while the game (or SFHud) is in front.</summary>
    public void SetScopedArmed(bool on)
    {
        if (_scopedArmed == on) return;
        _scopedArmed = on;
        if (_suspended) return;
        foreach (var (row, mods, key) in _scoped)
        {
            if (on && !_ids.ContainsKey(row.Def.Id)) Arm(row, mods, key);
            else if (!on && _ids.Remove(row.Def.Id, out int id)) _hk.Unregister(id);
        }
    }

    /// <summary>While the user is recording a new combo, nothing fires.</summary>
    public void Suspend(bool on)
    {
        if (_suspended == on) return;
        _suspended = on;
        Rebuild();
    }

    public string GestureFor(string action) => Rows.FirstOrDefault(r => r.Def.Id == action)?.Gesture ?? "";
}
