using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace SFHud.Services;

/// <summary>A game (or any app) the overlay attaches to.</summary>
public sealed class GameTarget
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";
    /// <summary>Process names without ".exe".</summary>
    public string[] Processes { get; set; } = [];
    /// <summary>Optional text the window title must contain (e.g. hl2.exe is shared by several Source games).</summary>
    public string? TitleHint { get; set; }
    public int? SteamAppId { get; set; }
    /// <summary>Follow whatever fullscreen / borderless app is in front.</summary>
    public bool IsAuto { get; set; }
    public bool IsCustom { get; set; }

    [JsonIgnore]
    public string Subtitle => IsAuto
        ? "Follows whichever app on your overlay whitelist is in front"
        : string.Join(" · ", Processes.Select(p => p + ".exe"));

    [JsonIgnore] public bool CanLaunch => SteamAppId is not null;
}

public static class GameCatalog
{
    public static IReadOnlyList<GameTarget> BuiltIn { get; } =
    [
        new() { Id = "gmod", Name = "Garry's Mod", Tag = "SOURCE · SANDBOX", Processes = ["gmod", "hl2"], TitleHint = "Garry", SteamAppId = 4000 },
        new() { Id = "auto", Name = "Any whitelisted app", Tag = "WHITELIST", IsAuto = true },
        new() { Id = "sbox", Name = "s&box", Tag = "SOURCE 2", Processes = ["sbox"], SteamAppId = 590830 },
        new() { Id = "tf2", Name = "Team Fortress 2", Tag = "SOURCE", Processes = ["tf_win64", "tf", "hl2"], TitleHint = "Team Fortress", SteamAppId = 440 },
        new() { Id = "cs2", Name = "Counter-Strike 2", Tag = "SOURCE 2", Processes = ["cs2"], SteamAppId = 730 },
        new() { Id = "rust", Name = "Rust", Tag = "SURVIVAL", Processes = ["RustClient"], SteamAppId = 252490 },
    ];

    public static IEnumerable<GameTarget> All(AppSettings s) => BuiltIn.Concat(s.CustomTargets);

    public static GameTarget Get(AppSettings s) => All(s).FirstOrDefault(t => t.Id == s.TargetId) ?? BuiltIn[0];

    /// <summary>Every built-in game's processes: the starting whitelist.</summary>
    public static List<string> DefaultWhitelist() =>
        BuiltIn.SelectMany(t => t.Processes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Processes the overlay may appear over: the whitelist plus the selected game.</summary>
    public static HashSet<string> Allowed(AppSettings s)
    {
        var set = new HashSet<string>(s.OverlayApps.Select(Clean).Where(p => p.Length > 0), StringComparer.OrdinalIgnoreCase);
        foreach (var p in Get(s).Processes) set.Add(p);
        return set;
    }

    /// <summary>"C:\…\Game.EXE " → "Game".</summary>
    public static string Clean(string process)
    {
        string p = System.IO.Path.GetFileName(process.Trim().Trim('"'));
        return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p[..^4] : p;
    }

    public static GameTarget CreateCustom(string processName, string title)
    {
        string name = string.IsNullOrWhiteSpace(title) ? processName : title.Length > 32 ? title[..32] : title;
        return new GameTarget
        {
            Id = "custom:" + processName.ToLowerInvariant(),
            Name = name,
            Tag = "CUSTOM",
            Processes = [processName],
            IsCustom = true,
        };
    }

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
    }
}
