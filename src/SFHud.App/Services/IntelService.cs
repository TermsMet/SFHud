using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SFHud.Services;

public enum IntelKind { Defcon, Alert, Server, Ai, Info }

public sealed record IntelEvent(DateTime At, IntelKind Kind, string Text)
{
    public string Time => At.ToString("HH:mm");
    public string KindText => Kind switch
    {
        IntelKind.Defcon => "DEFCON",
        IntelKind.Alert => "ALERT",
        IntelKind.Server => "SERVER",
        IntelKind.Ai => "AI",
        _ => "INFO",
    };
}

/// <summary>
/// Live server intel without touching the game: tails Garry's Mod's console.log (written when the
/// game runs with -condebug) and reads DEFCON changes, server/map and alert keywords from chat and
/// announcements. Rules are deterministic; the optional local model only adds a short summary,
/// alerts and standing orders. It never sets DEFCON.
/// </summary>
public sealed partial class IntelService : Observable
{
    public static IntelService Instance { get; } = new();

    private const int MaxRecent = 80;
    private static readonly TimeSpan AiMinGap = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<string> _recent = new();
    private string? _logPath;
    private long _pos;
    private string _partial = "";
    private DateTime _lastLineAt = DateTime.MinValue;
    private DateTime _lastAiAt = DateTime.MinValue;
    private int _linesSinceAi;
    private bool _aiRunning;
    private string _lastHeader = "";

    private int? _defcon;
    private string _defconSource = "";
    private DateTime? _defconAt;
    private string _server = "";
    private string _map = "";
    private string _summary = "";
    private string _orders = "";
    private string _feedStatus = "OFF";

    public ObservableCollection<IntelEvent> Events { get; } = new();

    public int? Defcon { get => _defcon; private set { if (Set(ref _defcon, value)) { Raise(nameof(DefconText)); Raise(nameof(HasDefcon)); Raise(nameof(DefconMeaning)); } } }
    public string DefconSource { get => _defconSource; private set => Set(ref _defconSource, value); }
    public DateTime? DefconAt { get => _defconAt; private set => Set(ref _defconAt, value); }
    public string Server { get => _server; private set => Set(ref _server, value); }
    public string Map { get => _map; private set => Set(ref _map, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Orders { get => _orders; private set => Set(ref _orders, value); }
    public string FeedStatus { get => _feedStatus; private set => Set(ref _feedStatus, value); }
    public string? LogPath => _logPath;

    public bool HasDefcon => Defcon is not null;
    public string DefconText => Defcon?.ToString() ?? "—";
    public string DefconMeaning => Defcon switch
    {
        1 => "MAXIMUM READINESS",
        2 => "HIGH ALERT",
        3 => "ELEVATED",
        4 => "GUARDED",
        5 => "NORMAL",
        _ => "UNKNOWN",
    };

    /// <summary>Alerts from the last few minutes, newest first, for the overlay.</summary>
    public IEnumerable<IntelEvent> ActiveAlerts =>
        Events.Where(e => e.Kind == IntelKind.Alert && DateTime.Now - e.At < TimeSpan.FromMinutes(5)).Take(2);

    /// <summary>(old, new) whenever the level changes, whatever the source.</summary>
    public event Action<int?, int?>? DefconChanged;
    public event Action<IntelEvent>? AlertRaised;
    public event Action? Changed;
    /// <summary>Every non-noise line (DefconService looks for announcements in it).</summary>
    public event Action<string>? LineIngested;
    public event Action<string, string>? ServerChanged;

    private IntelService() => _poll.Tick += (_, _) => Poll();

    public void Start()
    {
        if (!DataStore.Settings.IntelEnabled) { Stop(); return; }
        _logPath = ResolveLogPath();
        if (_logPath is null)
        {
            FeedStatus = "CONSOLE.LOG NOT FOUND";
        }
        else
        {
            Seed(_logPath);
            FeedStatus = "WATCHING CONSOLE.LOG";
        }
        _poll.Start();
        Changed?.Invoke();
    }

    public void Stop()
    {
        _poll.Stop();
        FeedStatus = "OFF";
        Changed?.Invoke();
    }

    public void Restart() { Stop(); _pos = 0; Start(); }

    // ================================================================ manual control

    public void SetManualDefcon(int? level)
    {
        if (level is < 1 or > 5) return;
        ApplyDefcon(level, "SET BY YOU");
    }

    private void ApplyDefcon(int? level, string source)
    {
        var old = Defcon;
        DefconSource = source;
        DefconAt = DateTime.Now;
        if (old == level) { Changed?.Invoke(); return; }
        Defcon = level;
        AddEvent(IntelKind.Defcon, level is null ? "DEFCON cleared" : $"DEFCON {level} · {DefconMeaning.ToLowerInvariant()} · {source.ToLowerInvariant()}");
        DefconChanged?.Invoke(old, level);
        Changed?.Invoke();
    }

    // ================================================================ log discovery

    private static string? ResolveLogPath()
    {
        string custom = DataStore.Settings.ConsoleLogPath.Trim().Trim('"');
        if (custom.Length > 0) return File.Exists(custom) ? custom : null;

        foreach (string lib in SteamLibraries())
        {
            string p = Path.Combine(lib, "steamapps", "common", "GarrysMod", "garrysmod", "console.log");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Steam library roots from the registry and libraryfolders.vdf.</summary>
    public static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string sp)
                roots.Add(sp.Replace('/', '\\'));
        }
        catch (Exception) { }
        roots.Add(@"C:\Program Files (x86)\Steam");

        var all = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            all.Add(root);
            try
            {
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in VdfPathRx().Matches(File.ReadAllText(vdf)))
                    all.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch (Exception) { }
        }
        return all.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex VdfPathRx();

    // ================================================================ tailing

    /// <summary>On start, read the tail of the log so a session already in progress shows up at once.</summary>
    private void Seed(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            _pos = fi.Length;
            if (DateTime.Now - fi.LastWriteTime > TimeSpan.FromMinutes(20)) return; // last session is stale
            long start = Math.Max(0, fi.Length - 96 * 1024);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string text = sr.ReadToEnd();
            var lines = text.Split('\n');
            if (start > 0 && lines.Length > 0) lines = lines[1..]; // first line is likely cut
            _seeding = true;
            foreach (var l in lines) Ingest(l.TrimEnd('\r'));
            _seeding = false;
        }
        catch (Exception) { _seeding = false; }
    }

    private bool _seeding;

    private void Poll()
    {
        if (_logPath is null)
        {
            // Game may have been installed or launched since; look again every few seconds.
            if (DateTime.Now.Second % 5 == 0 && (_logPath = ResolveLogPath()) is not null) { Seed(_logPath); FeedStatus = "WATCHING CONSOLE.LOG"; }
            return;
        }
        try
        {
            var fi = new FileInfo(_logPath);
            if (!fi.Exists) { FeedStatus = "CONSOLE.LOG MISSING"; return; }
            if (fi.Length < _pos) { _pos = 0; _partial = ""; } // log was rotated / truncated
            if (fi.Length > _pos)
            {
                using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(_pos, SeekOrigin.Begin);
                var buf = new byte[Math.Min(fi.Length - _pos, 512 * 1024)];
                int n = fs.Read(buf, 0, buf.Length);
                _pos += n;
                string chunk = _partial + Encoding.UTF8.GetString(buf, 0, n);
                int lastNl = chunk.LastIndexOf('\n');
                if (lastNl < 0) { _partial = chunk; return; }
                _partial = chunk[(lastNl + 1)..];
                foreach (var l in chunk[..lastNl].Split('\n')) Ingest(l.TrimEnd('\r'));
                _lastLineAt = DateTime.Now;
            }
            FeedStatus = DateTime.Now - fi.LastWriteTime < TimeSpan.FromMinutes(2) ? "LIVE · CONSOLE.LOG" : "IDLE · WAITING FOR GAME";
            MaybeRunAi();
        }
        catch (IOException) { /* file busy; next tick */ }
        catch (UnauthorizedAccessException) { FeedStatus = "CONSOLE.LOG LOCKED"; }
    }

    // ================================================================ rules

    [GeneratedRegex(@"\bdef\s?con\b(?<tail>.{0,48})", RegexOptions.IgnoreCase)]
    private static partial Regex DefconRx();

    [GeneratedRegex(@"\b([1-5]|one|two|three|four|five)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LevelRx();

    [GeneratedRegex(@"\bto\s+(?:def\s?con\s*)?(?:level\s*)?([1-5]|one|two|three|four|five)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ToLevelRx();

    [GeneratedRegex(@"\b(lock\s?down|breach(ed)?|code\s+(red|black)|k\.?o\.?s\.?\b|raid(ing)?|evacuat\w*|intruders?|jail\s?break|prison\s?break|escaped?\s+prisoners?|hostile|under\s+attack|all\s+units)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AlertRx();

    [GeneratedRegex(@"^(GHOOK|Warning|Failed|JOY_|Compact freed|Redownloading|Adding Filesystem|Mounted|Attempting|Host_|Cleaning|No Kinect|Using map cycle|Advanced Joystick|Clientside Lua|Lua Error|\[ERROR\]|Couldn't|Unknown command|Error|ConVarRef|SoundEmitter|CreateFragments|Precache|Requesting|Downloading|Sending|Connected to|Redirecting|Steam|Material|Model|Unable|Shutdown|Dropped|Disconnect|Missing|Loading|Loaded|Initializing|\s)", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRx();

    private static int? WordLevel(string s) => s.ToLowerInvariant() switch
    {
        "1" or "one" => 1, "2" or "two" => 2, "3" or "three" => 3, "4" or "four" => 4, "5" or "five" => 5, _ => null,
    };

    /// <summary>"DEFCON 3", "Defcon has been raised to 2", "DEFCON: FOUR", "defcon from 4 to 1".</summary>
    public static int? ParseDefcon(string line)
    {
        var m = DefconRx().Match(line);
        if (!m.Success) return null;
        string tail = m.Groups["tail"].Value;
        var to = ToLevelRx().Match(tail);
        if (to.Success) return WordLevel(to.Groups[1].Value);
        var lv = LevelRx().Match(tail);
        return lv.Success && lv.Index < 24 ? WordLevel(lv.Groups[1].Value) : null;
    }

    private void Ingest(string line)
    {
        if (line.Length == 0) return;

        // Connection banner: "<hostname>\nGamemode: X\nMap: Y\nPlayers: Z"
        if (line.StartsWith("Gamemode: ", StringComparison.Ordinal))
        {
            if (_lastHeader.Length > 0 && _lastHeader != Server) Server = _lastHeader;
            return;
        }
        if (line.StartsWith("Map: ", StringComparison.Ordinal))
        {
            string map = line[5..].Trim();
            if (map != Map)
            {
                Map = map;
                if (!_seeding || Events.Count == 0) AddEvent(IntelKind.Server, $"{(Server.Length > 0 ? Server : "Server")} · {map}");
                // New server: DEFCON from the old one no longer applies unless you set it.
                ServerChanged?.Invoke(Server, map);
            }
            return;
        }
        if (!line.Contains(':') && line.Length < 80 && !line.StartsWith('[')) _lastHeader = line.Trim();

        if (NoiseRx().IsMatch(line)) return;

        if (!_seeding) LineIngested?.Invoke(line);

        if (AlertRx().IsMatch(line))
        {
            var ev = AddEvent(IntelKind.Alert, Clip(line, 120));
            if (!_seeding) AlertRaised?.Invoke(ev);
        }

        _recent.Add(Clip(line, 220));
        if (_recent.Count > MaxRecent) _recent.RemoveAt(0);
        _linesSinceAi++;
        if (!_seeding) Changed?.Invoke();
    }

    private IntelEvent AddEvent(IntelKind kind, string text)
    {
        var ev = new IntelEvent(DateTime.Now, kind, text);
        // Don't repeat the same alert line spammed in chat.
        if (Events.FirstOrDefault() is { } top && top.Kind == kind && top.Text == text && (ev.At - top.At).TotalSeconds < 60) return top;
        Events.Insert(0, ev);
        while (Events.Count > 40) Events.RemoveAt(Events.Count - 1);
        Changed?.Invoke();
        return ev;
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ================================================================ AI layer

    private void MaybeRunAi()
    {
        if (!DataStore.Settings.IntelAiEnabled || _aiRunning || _linesSinceAi < 3) return;
        if (DateTime.Now - _lastAiAt < AiMinGap) return;
        if (!LocalAi.Instance.CanAutoUse) return; // not while you've unloaded the model
        _ = AnalyzeAsync();
    }

    /// <summary>Asks the local model for a summary of recent chat. Also used by the dashboard's ANALYZE button.</summary>
    public async Task<bool> AnalyzeAsync()
    {
        if (_aiRunning) return false;
        var lines = _recent.TakeLast(40).ToList();
        if (lines.Count == 0) { Summary = "No chat lines yet. Join a server with -condebug on."; return false; }

        _aiRunning = true;
        _lastAiAt = DateTime.Now;
        _linesSinceAi = 0;
        try
        {
            const string system =
                "You read recent chat and announcement lines from a Garry's Mod roleplay server and report its current state. " +
                "Reply with one JSON object only: {\"alerts\": [up to 3 short strings], " +
                "\"orders\": [up to 3 short standing orders], \"summary\": \"one short sentence\"}. " +
                "Only use facts stated in the lines. Use null or [] when the lines don't say.";
            string reply = await LocalAi.Instance.ChatAsync(system, string.Join("\n", lines), 220, json: true) ?? "";
            var j = LocalAi.ParseJsonLoose(reply);
            if (j is null) return false;

            string summary = (j["summary"]?.ToString() ?? "").Trim();
            if (summary.Length > 0 && summary != "null") Summary = Clip(summary, 180);
            Orders = string.Join("\n", Strings(j["orders"]).Take(3).Select(o => "◇ " + Clip(o, 90)));

            // DEFCON is never taken from the model: in testing a 0.8B model misread "DEFCON 2" as 1.
            // The deterministic rules (or you) own the level; the model only summarizes.
            foreach (var a in Strings(j["alerts"]).Take(2))
                if (!Events.Any(e => e.Kind == IntelKind.Ai && e.Text == a)) AddEvent(IntelKind.Ai, Clip(a, 120));
            Changed?.Invoke();
            return true;
        }
        finally { _aiRunning = false; }
    }

    private static IEnumerable<string> Strings(JsonNode? n) =>
        n is JsonArray a
            ? a.Select(x => x?.ToString().Trim() ?? "").Where(s => s.Length > 0 && s != "null")
            : Enumerable.Empty<string>();

    /// <summary>Recent lines as context for "ask the AI" on the dashboard.</summary>
    public string RecentContext(int n = 30) => string.Join("\n", _recent.TakeLast(n));
}
