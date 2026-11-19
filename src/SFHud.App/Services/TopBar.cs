using System;
using System.Collections.Generic;
using System.Linq;

namespace SFHud.Services;

/// <summary>One piece of the top bar: its text and the theme brush to draw it with.</summary>
/// <summary>One segment of the top bar: an optional small caption (SESSION), the value, and an optional trailing tag (ASSUMED).</summary>
public sealed record TopBarPart(string Text, string Brush, bool Bold = false, string Label = "", string Tag = "")
{
    public string Plain => string.Join(" ", new[] { Label, Text, Tag }.Where(s => s.Length > 0));
}

/// <summary>
/// The overlay's top bar (status strip): which items it shows and in what order, plus the shared
/// clock format (12 / 24 h, seconds, time zone and label) also used by the Clock element.
/// </summary>
public static class TopBar
{
    /// <summary>Every item the top bar can show: id, name in the settings list.</summary>
    public static IReadOnlyList<(string Id, string Name)> All { get; } = new[]
    {
        ("game", "Game (e.g. Garry's Mod)"),
        ("preset", "Server preset name"),
        ("profile", "Profile name"),
        ("server", "Server name (from console.log)"),
        ("session", "Session time"),
        ("total", "Total playtime"),
        ("clock", "Clock"),
        ("date", "Date"),
        ("defcon", "DEFCON level"),
    };

    public static string NameOf(string id) => All.FirstOrDefault(a => a.Id == id).Name ?? id;

    private static AppSettings S => DataStore.Settings;

    // ================================================================ clock

    /// <summary>Now, in the chosen time zone (falls back to local if the zone id is unknown).</summary>
    public static DateTime Now()
    {
        if (S.ClockTimeZone.Length == 0) return DateTime.Now;
        try { return TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.FindSystemTimeZoneById(S.ClockTimeZone)); }
        catch (Exception) { return DateTime.Now; }
    }

    public static string TimeText(DateTime t, bool? seconds = null)
    {
        bool sec = seconds ?? S.ClockSeconds;
        string fmt = S.Clock24h ? (sec ? "HH:mm:ss" : "HH:mm") : (sec ? "h:mm:ss tt" : "h:mm tt");
        return t.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string ClockText() => TimeText(Now()) + (S.ClockLabel.Trim().Length > 0 ? " " + S.ClockLabel.Trim().ToUpperInvariant() : "");

    /// <summary>Time zones for the picker: "" (this PC) first, then Windows' list.</summary>
    public static IReadOnlyList<string> TimeZoneIds()
    {
        var ids = new List<string> { "" };
        try { ids.AddRange(TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id)); } catch (Exception) { }
        return ids;
    }

    public static string TimeZoneName(string id)
    {
        if (id.Length == 0) return "This PC's local time";
        try { return TimeZoneInfo.FindSystemTimeZoneById(id).DisplayName; } catch (Exception) { return id; }
    }

    // ================================================================ parts

    /// <summary>The bar's content right now, in the user's order (empty items are skipped).</summary>
    public static List<TopBarPart> Compose(string targetName, string sessionText, bool sessionRunning)
    {
        var parts = new List<TopBarPart>();
        foreach (var id in S.TopBarItems)
        {
            switch (id)
            {
                case "game": parts.Add(new(targetName.ToUpperInvariant(), "B.Text", Bold: true)); break;
                case "preset": parts.Add(new(DataStore.Preset.Name.ToUpperInvariant(), "B.Amber")); break;
                case "profile": parts.Add(new(DataStore.Profile.Name.ToUpperInvariant(), "B.Dim")); break;
                case "server":
                    if (IntelService.Instance.Server is { Length: > 0 } srv) parts.Add(new(srv.Length > 32 ? srv[..32] + "…" : srv, "B.Dim"));
                    break;
                case "session": parts.Add(new(sessionRunning ? sessionText : "--:--:--", "B.Text", Label: "SESSION")); break;
                case "total":
                    S.PlaytimeSeconds.TryGetValue(S.TargetId, out double total);
                    parts.Add(new(GameCatalog.FormatDuration(total), "B.Text", Label: "TOTAL"));
                    break;
                case "clock": parts.Add(new(ClockText(), "B.Text")); break;
                case "date": parts.Add(new(Now().ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant(), "B.Dim")); break;
                case "defcon":
                    var svc = DefconService.Instance;
                    if (svc.Active is { } lvl)
                        parts.Add(new(lvl.Short, "defcon", Bold: true, Label: "DEFCON", Tag: svc.Status == DefconStatus.Assumed ? "ASSUMED" : svc.Status == DefconStatus.Stale ? "STALE" : ""));
                    break;
            }
        }
        return parts;
    }

    public static void Toggle(string id, bool on)
    {
        var list = S.TopBarItems;
        if (on && !list.Contains(id))
        {
            // Insert in the catalogue's natural order relative to what's already there.
            int order = All.ToList().FindIndex(a => a.Id == id);
            int at = list.FindIndex(x => All.ToList().FindIndex(a => a.Id == x) > order);
            if (at < 0) list.Add(id); else list.Insert(at, id);
        }
        else if (!on) list.Remove(id);
        DataStore.MarkDirty();
    }

    public static void Move(string id, int dir)
    {
        var list = S.TopBarItems;
        int i = list.IndexOf(id), j = i + dir;
        if (i < 0 || j < 0 || j >= list.Count) return;
        (list[i], list[j]) = (list[j], list[i]);
        DataStore.MarkDirty();
    }
}
