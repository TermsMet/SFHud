using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SFHud.Services;


/// <summary>A chat line split into who sent it and what they said.</summary>
public sealed record ChatLine(string Role, string Name, string Message, string Raw);

/// <summary>
/// SFHud Pro: watches server chat (Garry's Mod console.log) for what you told it to look after, for
/// example staff saying "EVENT in 10 MINUTES", and shows it on screen for a moment ("Event starting
/// soon"). Chat is split with the preset's chat format so roles and names are read correctly.
/// Everything runs on this PC; the optional local AI only helps with staff messages no rule matched.
/// </summary>
public static partial class ChatWatch
{
    /// <summary>(title, text, urgent, sound) for the overlay to show.</summary>
    public static event Action<string, string, bool, string>? Alert;
    /// <summary>A countdown the overlay should start (label, minutes).</summary>
    public static event Action<string, int>? CountdownRequested;

    private static readonly Dictionary<string, DateTime> Recent = new();
    private static DateTime _lastAi = DateTime.MinValue;

    public static void Start() => IntelService.Instance.LineIngested += OnLine;

    // ---------------------------------------------------------------- parsing

    /// <summary>Common GMod chat shapes, tried in order when no format is set.</summary>
    private static readonly Regex[] Builtin =
    {
        new(@"^\s*\[(?<role>[^\]]{1,32})\]\s*(?:\[(?<role2>[^\]]{1,32})\]\s*)?(?<name>[^:]{1,40}?)\s*:\s+(?<message>.+)$", RegexOptions.Compiled),
        new(@"^\s*\((?<role>[^)]{1,32})\)\s*(?<name>[^:]{1,40}?)\s*:\s+(?<message>.+)$", RegexOptions.Compiled),
        new(@"^\s*\*(?<role>[^*]{1,24})\*\s*(?<name>[^:]{1,40}?)\s*:\s+(?<message>.+)$", RegexOptions.Compiled),
        new(@"^\s*(?<role>[A-Z][A-Z ]{1,20})\s*[|»>]\s*(?<name>[^:]{1,40}?)\s*:\s+(?<message>.+)$", RegexOptions.Compiled),
        new(@"^\s*(?<name>[^:\[\]()]{1,40}?)\s*:\s+(?<message>.+)$", RegexOptions.Compiled),
    };

    /// <summary>
    /// Turns a format like "[{role}] {name}: {message}" into a pattern. Placeholders: {role} {name}
    /// {message} {channel} (counted as a role) and {any} (skipped). Everything else must match literally.
    /// </summary>
    public static Regex? FormatToRegex(string format, out string error)
    {
        error = "";
        format = format.Trim();
        if (format.Length == 0) return null;
        if (!format.Contains("{message}")) { error = "The format needs {message}."; return null; }
        var sb = new System.Text.StringBuilder("^\\s*");
        int i = 0;
        var used = new HashSet<string>();
        while (i < format.Length)
        {
            int open = format.IndexOf('{', i);
            if (open < 0) { sb.Append(Lit(format[i..])); break; }
            sb.Append(Lit(format[i..open]));
            int close = format.IndexOf('}', open);
            if (close < 0) { error = "A { has no matching }."; return null; }
            string name = format[(open + 1)..close].Trim().ToLowerInvariant();
            bool last = close == format.Length - 1;
            string group = name switch
            {
                "role" or "channel" => used.Add("role") ? "(?<role>.{1,40}?)" : "(?:.{1,40}?)",
                "name" => used.Add("name") ? "(?<name>.{1,48}?)" : "(?:.{1,48}?)",
                "message" => used.Add("message") ? (last ? "(?<message>.+)" : "(?<message>.+?)") : "(?:.+?)",
                "any" => ".*?",
                _ => "",
            };
            if (group.Length == 0) { error = $"Unknown placeholder {{{name}}}. Use {{role}}, {{name}}, {{message}}, {{channel}} or {{any}}."; return null; }
            sb.Append(group);
            i = close + 1;
        }
        sb.Append("\\s*$");
        try { return new Regex(sb.ToString(), RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
        catch (ArgumentException ex) { error = ex.Message; return null; }

        // Spaces in the format match any run of spaces.
        static string Lit(string s) => Regex.Escape(s).Replace("\\ ", "\\s*");
    }

    public static ChatLine? Parse(string raw, string format)
    {
        var custom = FormatToRegex(format, out _);
        foreach (var rx in custom is not null ? new[] { custom } : Builtin)
        {
            Match m;
            try { m = rx.Match(raw); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!m.Success) continue;
            string role = string.Join(" ", new[] { m.Groups["role"].Value, m.Groups["role2"].Value }.Where(s => s.Length > 0)).Trim();
            string name = m.Groups["name"].Value.Trim();
            string msg = m.Groups["message"].Value.Trim();
            if (msg.Length == 0) continue;
            return new ChatLine(role, name, msg, raw);
        }
        return null;
    }

    // ---------------------------------------------------------------- matching

    [GeneratedRegex(@"\b(?:in|within|starts? in|starting in|t-?)\s*(\d{1,3})\s*(?:m\b|min\b|mins\b|minutes?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex MinutesRx();

    [GeneratedRegex(@"\b(\d{1,3})\s*(?:m\b|min\b|mins\b|minutes?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex LooseMinutesRx();

    public static int? Minutes(string message)
    {
        var m = MinutesRx().Match(message);
        if (!m.Success) m = LooseMinutesRx().Match(message);
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) && n is > 0 and <= 240 ? n : null;
    }

    private static IEnumerable<string> List(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0);

    private static bool AnyIn(string haystack, string csv, bool wholeWord)
    {
        foreach (var w in List(csv))
        {
            if (!wholeWord) { if (haystack.Contains(w, StringComparison.OrdinalIgnoreCase)) return true; continue; }
            if (Regex.IsMatch(haystack, $@"(?<![\w]){Regex.Escape(w)}(?![\w])", RegexOptions.IgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Does this rule fire for the line? Used by the live feed and by the settings tester.</summary>
    public static bool Matches(ChatRule r, ChatLine c)
    {
        if (!r.Enabled) return false;
        bool sender = true;
        if (r.Roles.Trim().Length > 0 || r.Names.Trim().Length > 0)
            sender = (r.Roles.Trim().Length > 0 && c.Role.Length > 0 && AnyIn(c.Role, r.Roles, wholeWord: true))
                  || (r.Names.Trim().Length > 0 && c.Name.Length > 0 && AnyIn(c.Name, r.Names, wholeWord: false));
        if (!sender) return false;
        return r.Phrases.Trim().Length == 0 || AnyIn(c.Message, r.Phrases, wholeWord: true);
    }

    public static (string Title, string Text) Render(ChatRule r, ChatLine c)
    {
        int? n = Minutes(c.Message);
        string Fill(string s) => s.Replace("{message}", c.Message).Replace("{name}", c.Name).Replace("{role}", c.Role).Replace("{n}", n?.ToString() ?? "");
        string title = Fill(r.Title.Length > 0 ? r.Title : r.Name).ToUpperInvariant();
        if (n is int mins && r.Countdown) title = $"{title} · STARTING IN {mins} MIN";
        return (title.Trim(' ', '·'), Fill(r.Text.Length > 0 ? r.Text : "{message}"));
    }

    // ---------------------------------------------------------------- live feed

    private static void OnLine(string raw)
    {
        if (!Edition.Has(ProFeature.ChatAlerts) || !DataStore.Settings.ChatAlertsEnabled) return;
        var pre = DataStore.Preset;
        if (Parse(raw, pre.ChatFormat) is not { } c) return;
        bool matchedAny = false;
        foreach (var r in pre.ChatRules)
        {
            if (!Matches(r, c)) continue;
            matchedAny = true;
            // The same announcement is often repeated; show it once a minute at most.
            string key = r.Id + "|" + c.Message.ToLowerInvariant();
            if (Recent.TryGetValue(key, out var t) && DateTime.Now - t < TimeSpan.FromSeconds(60)) return;
            Recent[key] = DateTime.Now;
            if (Recent.Count > 200) foreach (var old in Recent.Where(kv => DateTime.Now - kv.Value > TimeSpan.FromMinutes(5)).Select(kv => kv.Key).ToList()) Recent.Remove(old);

            var (title, text) = Render(r, c);
            Alert?.Invoke(title, text, r.Urgent, r.Sound);
            if (r.Countdown && Minutes(c.Message) is int n) CountdownRequested?.Invoke((r.Title.Length > 0 && !r.Title.Contains('{') ? r.Title : r.Name).ToUpperInvariant(), n);
            return; // first matching rule wins
        }
        if (!matchedAny && DataStore.Settings.ChatEventsAi && LooksLikeAnnouncement(c.Message)) QueueAi(c);
    }

    // ---------------------------------------------------------------- local AI: upcoming events

    [GeneratedRegex(@"\b(?:event|training|tryouts?|meeting|briefing|formation|deploy(?:ment)?|patrol|raid|battle|mission|op(?:eration)?|restart|reboot|ceremony|inspection|drill|lockdown|rally|muster|vote|giveaway)s?\b|\b\d{1,2}[:.]\d{2}\b|\b(?:in|at)\s+\d{1,3}\s*(?:m|min|mins|minutes?|s|sec|seconds?|h|hours?)\b|\b(?:soon|shortly|starting|begins?|tonight|now)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnnouncementRx();

    /// <summary>Cheap filter first, so the model only sees lines that could announce something.</summary>
    public static bool LooksLikeAnnouncement(string message) => message.Length is >= 8 and <= 400 && AnnouncementRx().IsMatch(message);

    private static ChatLine? _queued;
    private static bool _aiBusy;

    /// <summary>One AI check at a time, at most every 8 s. A line that arrives meanwhile waits (the newest wins), it isn't dropped.</summary>
    private static void QueueAi(ChatLine c)
    {
        if (!LocalAi.Instance.CanAutoUse) return; // not while you've unloaded the model
        _queued = c;
        if (!_aiBusy) _ = DrainAsync();
    }

    private static async Task DrainAsync()
    {
        _aiBusy = true;
        try
        {
            while (_queued is { } c)
            {
                _queued = null;
                var wait = TimeSpan.FromSeconds(8) - (DateTime.Now - _lastAi);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                _lastAi = DateTime.Now;
                if (!LocalAi.Instance.CanAutoUse) break; // unloaded while waiting
                await AiCheckAsync(c);
            }
        }
        catch (Exception ex) { Log.Info("chat ai: " + ex.Message); }
        finally { _aiBusy = false; }
    }

    /// <summary>
    /// Chat no rule caught: the local model decides whether it announces something the player should get
    /// ready for (an event, training, deployment, restart…) and when. A yes shows a toast; with a time it
    /// also starts a countdown. Everything runs on this PC.
    /// </summary>
    private static async Task AiCheckAsync(ChatLine c)
    {
        const string system =
            "You read one chat message from a Garry's Mod roleplay server. Decide if it announces an upcoming event players should get ready for " +
            "(an event, training, tryouts, meeting, deployment, patrol, battle, server restart). Ignore jokes, questions, trades and normal roleplay talk. " +
            "Reply JSON only: {\"upcoming\": true|false, \"headline\": \"what, max 6 words\", \"minutes\": number of minutes until it starts or null}.";
        string who = c.Role.Length > 0 ? $"[{c.Role}] {c.Name}" : c.Name;
        string? reply = await LocalAi.Instance.ChatAsync(system, $"{who}: {c.Message}", 70, json: true);
        var j = LocalAi.ParseJsonLoose(reply);
        if (!string.Equals(j?["upcoming"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase)) return;
        string head = Clip(j?["headline"]?.ToString().Trim() ?? "", 48);
        if (head.Length == 0 || head == "null") head = "Upcoming event";
        // Minutes: what the message itself says beats the model's arithmetic.
        int? mins = Minutes(c.Message);
        if (mins is null && int.TryParse(j?["minutes"]?.ToString(), out int m) && m is > 0 and <= 240) mins = m;

        string key = "ai|" + head.ToLowerInvariant();
        if (Recent.TryGetValue(key, out var t) && DateTime.Now - t < TimeSpan.FromMinutes(2)) return;
        Recent[key] = DateTime.Now;

        string title = head.ToUpperInvariant() + (mins is int n ? $" · STARTING IN {n} MIN" : "");
        Alert?.Invoke(title, $"{c.Name}: {Clip(c.Message, 160)}", false, "");
        if (mins is int n2) CountdownRequested?.Invoke(head.ToUpperInvariant(), n2);
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
