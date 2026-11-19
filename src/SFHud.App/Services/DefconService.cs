using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Threading;
using SFHud.Interop;

namespace SFHud.Services;

public sealed record DefconSuggestion(DefconLevel Level, double Confidence, string Source, string Evidence, DateTime At, bool FromAi)
{
    public string ConfidenceText => $"{Confidence:P0}";
    public string Text => $"{Level.Label} · {Source} · {ConfidenceText} sure";
}

public sealed record DefconMatch(DefconLevel Level, double Confidence, string Evidence);

/// <summary>
/// The active DEFCON level for the current preset, its <see cref="DefconStatus"/>, and everything that
/// can change it.
///
/// Fallback and detection are kept apart: on startup (and preset switch) the overlay shows the
/// fallback level (DEFCON 6) labelled ASSUMED, never as verified. The screen region (OCR of the GMod
/// window only) is the primary detector: once it reads the same level N times in a row with high
/// confidence, that level is applied as AUTO-DETECTED. If confirming reads stop, it shows as STALE,
/// then drops back to the ASSUMED fallback after a timeout.
///
/// Other sources (console.log, a status URL, pasted text) only suggest unless you opted in to
/// auto-apply. With "AI switches DEFCON automatically" (on by default) the local AI applies a level as
/// AI-DETECTED when the words it cites are in the text; the "Back to default DEFCON" key (Ctrl+Alt+End)
/// undoes any automatic switch and blocks that level for 10 minutes.
/// </summary>
public sealed partial class DefconService : Observable
{
    private static DefconService? _instance;
    public static DefconService Instance => _instance ??= new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly DispatcherTimer _tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _nextUrl = DateTime.MinValue, _nextOcr = DateTime.MinValue;
    private string _lastOcrLevel = "";
    private int _streak;
    private DateTime _nextAi = DateTime.MinValue;
    private string _aiSeen = "";
    private readonly Dictionary<string, DateTime> _blocked = new();
    private DefconStatus _status = DefconStatus.Assumed;
    private DateTime? _lastVerified;
    private (string LevelId, DateTime Until) _snooze;
    private DefconSuggestion? _pending;
    private string _ocrText = "", _urlText = "", _sourceText = "";
    private bool _busy;

    public Func<IntPtr>? GameWindow { get; set; }
    public Func<bool>? GameInFront { get; set; }

    public ObservableCollection<string> History { get; } = new();

    public DefconSuggestion? Pending { get => _pending; private set { if (Set(ref _pending, value)) SuggestionChanged?.Invoke(value); } }
    public string LastOcrText { get => _ocrText; private set => Set(ref _ocrText, value); }
    public string LastUrlText { get => _urlText; private set => Set(ref _urlText, value); }
    public string ActiveSource { get => _sourceText; private set => Set(ref _sourceText, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    private static DefconConfig Cfg => DataStore.Preset.Defcon;
    private static DetectionSettings Det => Cfg.Detection;
    public static bool Enabled => DataStore.Preset.DefconEnabled;

    public DefconLevel? Active => Enabled ? Cfg.Active : null;

    public DefconStatus Status
    {
        get => _status;
        private set { if (Set(ref _status, value)) { Raise(nameof(StatusLabel)); Raise(nameof(StatusDetail)); StatusChanged?.Invoke(); } }
    }

    /// <summary>When the screen region last confirmed the active level.</summary>
    public DateTime? LastVerified => _lastVerified;

    /// <summary>Short tag shown next to the level: ASSUMED / AUTO-DETECTED / STALE / SET BY YOU.</summary>
    public string StatusLabel => Status switch
    {
        DefconStatus.Assumed => "ASSUMED",
        DefconStatus.Detected => "AUTO-DETECTED",
        DefconStatus.Stale => "STALE",
        DefconStatus.AiDetected => "AI-DETECTED",
        _ => "SET",
    };

    /// <summary>One line explaining how far to trust the level.</summary>
    public string StatusDetail
    {
        get
        {
            string ago = _lastVerified is { } t ? Ago(DateTime.Now - t) : "";
            return Status switch
            {
                DefconStatus.Assumed => !Det.Enabled || !Det.Ocr
                    ? "Fallback · turn on screen-region reading to auto-detect"
                    : _streak > 0 ? $"Verifying reading {_streak}/{Math.Max(1, Det.VerifyReads)}…" : "Awaiting reliable detection",
                DefconStatus.Detected => $"Screen · {Math.Max(1, Det.VerifyReads)} matching reads · verified {ago}",
                DefconStatus.AiDetected => ActiveSource + " · your default-DEFCON key undoes it",
                DefconStatus.Stale => $"Last verified {ago} · back to DEFCON {Det.FallbackNumber} in {Ago(TimeSpan.FromSeconds(Math.Max(0, Det.AssumeAfterSeconds - (DateTime.Now - (_lastVerified ?? DateTime.Now)).TotalSeconds)), suffix: false)}",
                _ => ActiveSource.Length > 0 ? ActiveSource : "set by you",
            };
        }
    }

    /// <summary>A few words for the small in-game badge (the full explanation is in the SFHud window).</summary>
    public string StatusShort
    {
        get
        {
            string ago = _lastVerified is { } t ? Ago(DateTime.Now - t) : "";
            return Status switch
            {
                DefconStatus.Assumed => _streak > 0 ? $"verifying {_streak}/{Math.Max(1, Det.VerifyReads)}" : "not verified",
                DefconStatus.Detected => "verified " + ago,
                DefconStatus.Stale => "last seen " + ago,
                DefconStatus.AiDetected => "AI · " + ago,
                _ => "set by you",
            };
        }
    }

    /// <summary>Theme brush key for <see cref="StatusLabel"/>.</summary>
    public string StatusBrush => Status switch
    {
        DefconStatus.Detected => "B.Ok",
        DefconStatus.AiDetected => "B.Ok",
        DefconStatus.Stale => "B.Amber",
        DefconStatus.Assumed => "B.Dim",
        _ => "B.Accent",
    };

    public event Action? StatusChanged;

    /// <summary>The safe default: DEFCON 6, or the least-alert level if the preset has no 6.</summary>
    public DefconLevel? Fallback => Cfg.ByNumber(Det.FallbackNumber) ?? Cfg.Levels.LastOrDefault();

    private static string Ago(TimeSpan t, bool suffix = true)
    {
        string s = t.TotalSeconds < 60 ? $"{Math.Max(0, (int)t.TotalSeconds)}s" : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return suffix ? s + " ago" : s;
    }

    /// <summary>(old, new) whenever the active level changes.</summary>
    public event Action<DefconLevel?, DefconLevel?>? LevelChanged;
    public event Action<DefconSuggestion?>? SuggestionChanged;

    private DefconService()
    {
        _tick.Tick += (_, _) => Tick();
        IntelService.Instance.LineIngested += OnConsoleLine;
        DataStore.ContextChanged += () =>
        {
            Pending = null;
            // Only a different preset (another server) is "unknown server" → assumed fallback.
            // Editing levels also raises ContextChanged and must not drop a verified reading.
            string key = DataStore.Profile.Id + "/" + DataStore.Preset.Id;
            if (key != _contextKey) { _contextKey = key; if (_tick.IsEnabled) AssumeFallback(quiet: true); }
            Raise(nameof(Active));
        };
    }

    private string _contextKey = "";

    public void Start()
    {
        // Step 1 of the detection flow: show the fallback immediately, clearly labelled ASSUMED.
        _contextKey = DataStore.Profile.Id + "/" + DataStore.Preset.Id;
        AssumeFallback(quiet: true);
        _tick.Start();
    }

    public void Stop() => _tick.Stop();

    /// <summary>Show the fallback (DEFCON 6) as ASSUMED. Quiet = no toast/sound (startup, preset switch).</summary>
    private void AssumeFallback(bool quiet)
    {
        if (!Enabled) return;
        _streak = 0;
        _lastOcrLevel = "";
        _lastVerified = null;
        if (!Det.AssumeOnStartup && quiet && Cfg.Active is not null) { Status = DefconStatus.Set; return; }
        Apply(Fallback, DefconStatus.Assumed, "assumed · awaiting reliable detection", quiet);
    }

    // ================================================================ set / accept

    /// <summary>Set by you (buttons, keys, menu).</summary>
    public void SetLevel(DefconLevel? level, string source) => Apply(level, DefconStatus.Set, source);

    private void Apply(DefconLevel? level, DefconStatus status, string source, bool quiet = false)
    {
        if (!Enabled) return;
        var old = Cfg.Active;
        Cfg.ActiveLevelId = level?.Id ?? "";
        ActiveSource = source;
        Status = status;
        Raise(nameof(StatusDetail));
        if (Pending is { } p && (level is null || p.Level.Id == level.Id)) Pending = null;
        DataStore.MarkDirty();
        if (old?.Id == level?.Id) { Raise(nameof(Active)); return; }
        History.Insert(0, $"{DateTime.Now:HH:mm}  {(level?.Label ?? "cleared")} · {source}");
        while (History.Count > 30) History.RemoveAt(History.Count - 1);
        Raise(nameof(Active));
        if (!quiet) LevelChanged?.Invoke(old, level);
    }

/// <summary>
    /// The "default DEFCON" key: back to the fallback level (DEFCON 6 unless changed), shown as ASSUMED.
    /// Whatever was auto-detected is blocked for 10 minutes, so the same reading doesn't switch it straight back;
    /// a different level, or one you set yourself, still works.
    /// </summary>
    public void ResetToDefault()
    {
        if (!Enabled) return;
        if (Cfg.Active is { } cur && cur.Id != Fallback?.Id && Status is DefconStatus.Detected or DefconStatus.Stale or DefconStatus.AiDetected)
            _blocked[cur.Id] = DateTime.Now.AddMinutes(10);
        Pending = null;
        _streak = 0;
        _lastOcrLevel = "";
        _lastVerified = null;
        Apply(Fallback, DefconStatus.Assumed, "back to default · by you");
    }

    private bool Blocked(DefconLevel l) => _blocked.TryGetValue(l.Id, out var until) && DateTime.Now < until;

    public void SetById(string id, string source = "set by you") => SetLevel(Cfg.Levels.FirstOrDefault(l => l.Id == id), source);

    /// <summary>Step through the list: up = towards the first (most alert) level.</summary>
    public void Step(int dir)
    {
        var levels = Cfg.Levels;
        if (!Enabled || levels.Count == 0) return;
        int i = Cfg.Active is { } a ? levels.IndexOf(a) : (dir < 0 ? levels.Count : -1);
        i = Math.Clamp(i + dir, 0, levels.Count - 1);
        SetLevel(levels[i], "set by you");
    }

    public void Accept()
    {
        if (Pending is not { } p) return;
        SetLevel(p.Level, $"{p.Source} · confirmed");
    }

    public void Dismiss()
    {
        if (Pending is not { } p) return;
        _snooze = (p.Level.Id, DateTime.Now.AddMinutes(5));
        Pending = null;
    }

    private void Suggest(DefconMatch m, string source, bool fromAi)
    {
        if (!Enabled || !Det.Enabled) return;
        if (Cfg.ActiveLevelId == m.Level.Id) { if (Pending?.Level.Id == m.Level.Id) Pending = null; return; }
        if (_snooze.LevelId == m.Level.Id && DateTime.Now < _snooze.Until) return;

        if (Blocked(m.Level)) return;
        bool auto = !fromAi && (Det.AutoApply == AutoApplyMode.HighConfidence || AiAuto) && m.Confidence >= Det.Threshold;
        if (auto) { SetLevel(m.Level, $"{source} · auto {m.Confidence:P0}"); return; }
        if (Pending is { } cur && cur.Level.Id == m.Level.Id && cur.Confidence >= m.Confidence) return;
        Pending = new DefconSuggestion(m.Level, Math.Round(m.Confidence, 2), source, Clip(m.Evidence, 140), DateTime.Now, fromAi);
    }

    // ================================================================ matching

    [GeneratedRegex(@"\b(?:def\s?con|threat\s*level|alert\s*level|alert\s*status|readiness(?:\s*level)?|red\s?con|condition)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KeywordRx();

    [GeneratedRegex(@"^\s*[:#=\-–—]?\s*(?:level\s*|is\s*(?:now\s*)?|now\s*|at\s*|set\s*(?:to\s*)?|=\s*)?(\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DirectRx();

    [GeneratedRegex(@"\b(?:to|into)\s+(?:def\s?con\s*)?(?:level\s*)?(\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ToRx();

    private static int? Word(string s) => s.ToLowerInvariant() switch
    {
        "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10,
        _ => int.TryParse(s, out int n) ? n : null,
    };

    /// <summary>Number announced in a line ("DEFCON 3", "defcon raised to 2", "Threat level: FOUR"), with how sure the rule is.</summary>
    public static (int Level, double Confidence)? ParseNumber(string line)
    {
        foreach (Match k in KeywordRx().Matches(line))
        {
            string tail = line[(k.Index + k.Length)..];
            if (tail.Length > 60) tail = tail[..60];
            var to = ToRx().Match(tail);
            if (to.Success && Word(to.Groups[1].Value) is int t) return (t, 0.9);
            var d = DirectRx().Match(tail);
            if (d.Success && Word(d.Groups[1].Value) is int n) return (n, 0.93);
        }
        return null;
    }

    /// <summary>Matches text against the preset's levels (numbers first, then names and aliases).</summary>
    public static DefconMatch? Match(string text, bool dedicatedRegion = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cfg = Cfg;
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (ParseNumber(line) is { } hit && cfg.ByNumber(hit.Level) is { } lvl)
                return new DefconMatch(lvl, hit.Confidence, line);
        }
        // Names / aliases: only trusted near an alert keyword, or when the text is a dedicated HUD region.
        bool keyword = KeywordRx().IsMatch(text);
        foreach (var l in cfg.Levels)
        {
            foreach (var word in Words(l))
            {
                var rx = new Regex(@"\b" + Regex.Escape(word) + @"\b", RegexOptions.IgnoreCase);
                var m = rx.Match(text);
                if (!m.Success) continue;
                if (dedicatedRegion) return new DefconMatch(l, text.Trim().Length <= word.Length + 14 ? 0.88 : 0.78, text.Trim());
                if (keyword) return new DefconMatch(l, 0.72, text.Trim());
            }
        }
        return null;
    }

    private static string[] Words(DefconLevel l)
    {
        var list = l.Aliases.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        // Generic names ("Level 3", "Normal") are too common to trust on their own.
        if (l.Name.Length >= 4 && !Regex.IsMatch(l.Name, @"^(level\s*\d+|normal|guarded|elevated|quiet)$", RegexOptions.IgnoreCase)) list.Add(l.Name);
        return list.Where(w => w.Length >= 3).ToArray();
    }

    // ================================================================ sources

    private void OnConsoleLine(string line)
    {
        if (!Enabled || !Det.Enabled || !Det.ConsoleLog) return;
        if (ParseNumber(line) is { } hit && Cfg.ByNumber(hit.Level) is { } lvl)
            Suggest(new DefconMatch(lvl, hit.Confidence, line), "console.log", false);
    }

    /// <summary>Text the user pasted (server status, announcement, MOTD). Rules first, then AI if allowed.</summary>
    public async Task<string> AnalyzeTextAsync(string text)
    {
        if (!Enabled) return "DEFCON is turned off for this preset.";
        if (Match(text) is { } m)
        {
            Suggest(m with { Confidence = Math.Min(0.95, m.Confidence + 0.02) }, "pasted text", false);
            return $"Found {m.Level.Label} ({m.Confidence:P0}). Confirm it below.";
        }
        if (!Det.AiAssist || !LocalAi.Instance.CanRun) return "No DEFCON level found in that text.";
        var ai = await AiGuessAsync(text, "pasted text");
        return ai ?? "No DEFCON level found in that text.";
    }

    /// <summary>Runs every enabled source once, then asks the AI if nothing rule-based matched.</summary>
    public async Task<string> ScanNowAsync()
    {
        if (!Enabled) return "DEFCON is turned off for this preset.";
        Busy = true;
        try
        {
            string report = "";
            if (Det.Ocr) report += (await RunOcrAsync()) + "\n";
            if (Det.StatusUrl.Length > 0) report += (await RunUrlAsync()) + "\n";
            if (Pending is null && Det.AiAssist && LocalAi.Instance.CanRun)
            {
                string ctx = string.Join("\n", new[] { LastOcrText, LastUrlText, IntelService.Instance.RecentContext(25) }.Where(s => s.Length > 0));
                if (ctx.Trim().Length > 0) report += (await AiGuessAsync(ctx, "AI") ?? "AI found nothing.") + "\n";
            }
            return report.Trim().Length == 0 ? "Nothing to scan. Turn on a source (screen region or status URL) first." : report.Trim();
        }
        finally { Busy = false; }
    }

    private void Tick()
    {
        if (!Enabled) return;
        var now = DateTime.Now;
        UpdateFreshness(now);
        if (!Det.Enabled || Busy || !Edition.Has(ProFeature.DefconAutoDetect)) return;
        if (Det.StatusUrl.Length > 0 && now >= _nextUrl)
        {
            _nextUrl = now.AddSeconds(Math.Max(15, Det.StatusUrlSeconds));
            _ = RunUrlAsync();
        }
        if (Det.Ocr && now >= _nextOcr && GameInFront?.Invoke() == true)
        {
            _nextOcr = now.AddSeconds(Math.Max(2, Det.OcrSeconds));
            _ = RunOcrAsync();
        }
        if (AiAuto && now >= _nextAi && GameWindow?.Invoke() is { } g && g != IntPtr.Zero)
        {
            _nextAi = now.AddSeconds(Math.Clamp(Det.AiAutoSeconds, 10, 300));
            _ = AiWatchAsync();
        }
    }

    /// <summary>Auto-switching by the local AI is on (Pro, model installed, both switches on).</summary>
    private static bool AiAuto => Det.Enabled && Det.AiAssist && Det.AiAutoApply && LocalAi.Instance.CanRun;

    /// <summary>Gives the AI the newest console / screen / status text, only when there is something new in it.</summary>
    private async Task AiWatchAsync()
    {
        string ctx = string.Join("\n", new[] { LastOcrText, LastUrlText, Det.ConsoleLog ? IntelService.Instance.RecentContext(15) : "" }.Where(s => s.Length > 0)).Trim();
        if (ctx.Length < 8 || ctx == _aiSeen || Busy) return;
        _aiSeen = ctx;
        // Rules first: they are exact and free. The AI only looks when the rules found nothing.
        if (Match(ctx) is not null) return;
        Busy = true;
        try { await AiGuessAsync(ctx, "watching"); }
        catch (Exception ex) { Log.Info("defcon ai: " + ex.Message); }
        finally { Busy = false; }
    }

    /// <summary>Detected → Stale when confirming reads stop; Stale → Assumed fallback after the timeout.</summary>
    private void UpdateFreshness(DateTime now)
    {
        if (_lastVerified is not { } t || Status is DefconStatus.Set or DefconStatus.Assumed or DefconStatus.AiDetected) return;
        double age = (now - t).TotalSeconds;
        if (Status == DefconStatus.Detected && age > Math.Max(5, Det.StaleAfterSeconds))
        {
            Status = DefconStatus.Stale;
            History.Insert(0, $"{now:HH:mm}  detection lost · {Cfg.Active?.Label} kept as stale");
        }
        else if (Status == DefconStatus.Stale && age > Math.Max(Det.StaleAfterSeconds + 5, Det.AssumeAfterSeconds))
        {
            Apply(Fallback, DefconStatus.Assumed, $"assumed · no reading for {Ago(now - t, suffix: false)}");
            _lastVerified = null;
            return;
        }
        Raise(nameof(StatusDetail)); // keeps "verified 12s ago" ticking
    }

    public async Task<string> RunOcrAsync()
    {
        IntPtr game = GameWindow?.Invoke() ?? IntPtr.Zero;
        if (!Edition.Has(ProFeature.DefconAutoDetect)) return "Screen: reading the level off the screen is part of SFHud Pro.";
        if (game == IntPtr.Zero) return "Screen: no game window found. Start your game (borderless or windowed), or pick it on the Games page, then scan again.";
        if (!OcrService.Available) return "Screen: Windows OCR isn't available (add a language with OCR support in Windows settings).";
        // Only the configured region of the game window is captured.
        string? text = await OcrService.ReadRegionAsync(game, Det.OcrX, Det.OcrY, Det.OcrW, Det.OcrH);
        if (text is null) return "Screen: capture failed.";
        LastOcrText = text.Trim();
        var m = Match(text, dedicatedRegion: true);
        if (m is null || m.Confidence < Det.VerifyConfidence)
        {
            // An unreadable frame breaks the streak; the level itself goes stale by time, not by one bad frame.
            _lastOcrLevel = "";
            _streak = 0;
            Raise(nameof(StatusDetail));
            return m is null ? $"Screen: read \"{Clip(LastOcrText, 60)}\", no level found." : $"Screen: {m.Level.Label} only {m.Confidence:P0} sure, ignored.";
        }

        // Verification: the same level, read N times in a row, before anything changes.
        _streak = _lastOcrLevel == m.Level.Id ? _streak + 1 : 1;
        _lastOcrLevel = m.Level.Id;
        int need = Math.Clamp(Det.VerifyReads, 1, 10);
        double conf = Math.Min(0.99, m.Confidence + 0.03 * (_streak - 1));

        if (_streak < need)
        {
            Raise(nameof(StatusDetail));
            return $"Screen: {m.Level.Label} ({conf:P0}) · verifying {_streak}/{need}.";
        }

        if (Det.AutoApplyVerifiedScreen && !Blocked(m.Level))
        {
            _lastVerified = DateTime.Now;
            if (Cfg.ActiveLevelId == m.Level.Id && Status == DefconStatus.Detected) { Raise(nameof(StatusDetail)); }
            else Apply(m.Level, DefconStatus.Detected, $"screen · {need} matching reads · {conf:P0}");
            return $"Screen: {m.Level.Label} verified ({need}/{need} reads, {conf:P0}).";
        }

        Suggest(m with { Confidence = conf }, "screen", false);
        return $"Screen: {m.Level.Label} ({conf:P0}) · verified, waiting for you to confirm.";
    }

    public async Task<string> RunUrlAsync()
    {
        if (!Edition.Has(ProFeature.DefconAutoDetect)) return "Status URL: part of SFHud Pro.";
        string url = Det.StatusUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return "Status URL: not a valid http(s) address.";
        try
        {
            using var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            string body = await resp.Content.ReadAsStringAsync();
            if (body.Length > 64 * 1024) body = body[..(64 * 1024)];
            string text = body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? Flatten(body) : Regex.Replace(body, "<[^>]+>", " ");
            LastUrlText = Clip(Regex.Replace(text, @"\s+", " ").Trim(), 400);
            if (Match(text) is { } m) { Suggest(m, "status URL", false); return $"Status URL: {m.Level.Label} ({m.Confidence:P0})."; }
            // JSON like {"defcon": 3}
            var num = Regex.Match(body, "\"(?:defcon|level|alert|threat)[a-z_]*\"\\s*:\\s*\"?(\\d{1,2})", RegexOptions.IgnoreCase);
            if (num.Success && Cfg.ByNumber(int.Parse(num.Groups[1].Value)) is { } lvl)
            {
                Suggest(new DefconMatch(lvl, 0.95, num.Value), "status URL", false);
                return $"Status URL: {lvl.Label} (95%).";
            }
            return "Status URL: no level found.";
        }
        catch (Exception ex) { return "Status URL: " + ex.Message; }
    }

    private static string Flatten(string json)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            void Walk(JsonNode? n, string key)
            {
                switch (n)
                {
                    case JsonObject o: foreach (var kv in o) Walk(kv.Value, kv.Key); break;
                    case JsonArray a: foreach (var x in a) Walk(x, key); break;
                    case null: break;
                    default: sb.Append(key).Append(": ").Append(n.ToString()).Append('\n'); break;
                }
            }
            Walk(JsonNode.Parse(json), "");
            return sb.ToString();
        }
        catch (Exception) { return json; }
    }

    /// <summary>Asks the local model which level the text implies. Switches by itself only when auto-switching is on and the cited words check out.</summary>
    public async Task<string?> AiGuessAsync(string text, string source)
    {
        var levels = string.Join("\n", Cfg.Levels.Select(l => $"- id \"{l.Id}\": {l.Label}{(l.Description.Length > 0 ? " — " + l.Description : "")}{(l.Aliases.Length > 0 ? " (also: " + l.Aliases + ")" : "")}"));
        string system =
            "You decide which alert level a Garry's Mod roleplay server is at, from the text given. " +
            "Reply with one JSON object only: {\"id\": \"<level id or null>\", \"confidence\": 0.0-1.0, \"evidence\": \"the exact words that say so\"}. " +
            "Use null when the text doesn't clearly state a level. Do not guess from tone.\nLevels:\n" + levels;
        string? reply = await LocalAi.Instance.ChatAsync(system, Clip(text, 2500), 120, json: true);
        var j = LocalAi.ParseJsonLoose(reply);
        string id = j?["id"]?.ToString() ?? "";
        var lvl = Cfg.Levels.FirstOrDefault(l => l.Id == id);
        if (lvl is null) return reply is null ? "AI unavailable: " + LocalAi.Instance.LastError : "AI: no clear level in that text.";
        double conf = double.TryParse(j?["confidence"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0.5;
        string evidence = j?["evidence"]?.ToString() ?? "";
        // Small local models are overconfident: cap it, and distrust words that are not in the text.
        conf = Math.Clamp(conf, 0.1, 0.75);
        if (evidence.Length > 0 && !text.Contains(evidence.Trim('"', ' ', '.'), StringComparison.OrdinalIgnoreCase)) conf = Math.Min(conf, 0.4);
        string ev = evidence.Trim('"', ' ', '.');
        bool cited = ev.Length >= 3 && text.Contains(ev, StringComparison.OrdinalIgnoreCase);
        // Small models misread numbers ("DEFCON 2" → 1): if the cited words hold an exact number, the number wins.
        if (cited && ParseNumber(ev) is { } exact && Cfg.ByNumber(exact.Level) is { } byNum && byNum.Id != lvl.Id) lvl = byNum;
        if (AiAuto && cited && conf >= Det.AiMinConfidence && !Blocked(lvl))
        {
            if (Cfg.ActiveLevelId == lvl.Id) return $"AI: still {lvl.Label}.";
            _lastVerified = DateTime.Now;
            Apply(lvl, DefconStatus.AiDetected, $"AI · \"{Clip(ev, 50)}\" · {conf:P0}");
            return $"AI switched to {lvl.Label} ({conf:P0}).";
        }
        Suggest(new DefconMatch(lvl, conf, evidence), source == "AI" ? "AI" : $"AI · {source}", true);
        return $"AI suggests {lvl.Label} ({conf:P0}). Confirm or dismiss it.";
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
