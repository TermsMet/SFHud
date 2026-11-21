using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SFHud.Interop;
using Windows.Media.Control;

namespace SFHud.Services;

/// <summary>A player Windows currently reports (one media session).</summary>
public sealed record MediaSource(string Id, string App, string Display, bool Playing);

/// <summary>
/// Now Playing + play / pause / skip through Windows' own media session API (the same one the volume
/// flyout and your keyboard's media keys use). Works with the Steam overlay browser, Chrome, Brave,
/// Edge, Opera, Vivaldi and other Chromium browsers, Firefox / LibreWolf and Spotify. Nothing is
/// injected into Steam, the game or the browser.
/// </summary>
public sealed class MediaService : Observable
{
    private static MediaService? _instance;
    public static MediaService Instance => _instance ??= new();

    private GlobalSystemMediaTransportControlsSessionManager? _mgr;
    private GlobalSystemMediaTransportControlsSession? _session;
    // Re-check every couple of seconds: Windows only raises SessionsChanged when a player appears or
    // disappears, not when a *different* existing player starts playing.
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
    private string _title = "";
    private string _artist = "";
    private string _status = "Not started";
    private bool _playing;
    private bool _hasSession;
    private string _sourceName = "";
    private string _elsewhere = "";
    private MediaMode? _elsewhereMode;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool Playing { get => _playing; private set => Set(ref _playing, value); }
    public bool HasSession { get => _hasSession; private set => Set(ref _hasSession, value); }
    public string SourceName { get => _sourceName; private set => Set(ref _sourceName, value); }

    /// <summary>A player that is playing but isn't the selected source ("Brave"), so the UI can offer to switch.</summary>
    public string PlayingElsewhere { get => _elsewhere; private set => Set(ref _elsewhere, value); }
    /// <summary>The source to switch to for <see cref="PlayingElsewhere"/>.</summary>
    public MediaMode? PlayingElsewhereMode => _elsewhereMode;

    public event Action? Changed;

    private static AppSettings S => DataStore.Settings;

    // ================================================================ sources the user can pick

    /// <summary>The choices offered in the source pickers, in display order.</summary>
    public static IReadOnlyList<MediaMode> Choices { get; } = new[]
    {
        MediaMode.Auto, MediaMode.SteamBrowser, MediaMode.ChromeBrave, MediaMode.Chrome, MediaMode.Brave,
        MediaMode.Edge, MediaMode.Opera, MediaMode.Vivaldi, MediaMode.Firefox, MediaMode.Spotify, MediaMode.AnyPlayer,
    };

    public static string Describe(MediaMode m) => m switch
    {
        MediaMode.Auto => "Auto (whatever is playing)",
        MediaMode.SteamBrowser => "Steam overlay browser",
        MediaMode.ChromeBrave => "Any Chromium browser (Chrome, Brave, Edge, Opera…)",
        MediaMode.Chrome => "Google Chrome",
        MediaMode.Brave => "Brave",
        MediaMode.Edge => "Microsoft Edge",
        MediaMode.Opera => "Opera / Opera GX",
        MediaMode.Vivaldi => "Vivaldi",
        MediaMode.Firefox => "Firefox / LibreWolf",
        MediaMode.Spotify => "Spotify",
        _ => "Windows' current player",
    };

    public static string Short(MediaMode m) => m switch
    {
        MediaMode.Auto => "AUTO",
        MediaMode.SteamBrowser => "STEAM BROWSER",
        MediaMode.ChromeBrave => "CHROMIUM",
        MediaMode.AnyPlayer => "CURRENT PLAYER",
        _ => m.ToString().ToUpperInvariant(),
    };

    /// <summary>Badge text: the selected source, plus the app actually followed in Auto / family modes.</summary>
    public string ModeLabel
    {
        get
        {
            if (S.MediaSessionId.Length > 0) return "PINNED · " + (SourceName.Length > 0 ? SourceName.ToUpperInvariant() : Friendly(S.MediaSessionId).ToUpperInvariant());
            string mode = Short(S.MediaMode);
            bool family = S.MediaMode is MediaMode.Auto or MediaMode.ChromeBrave or MediaMode.AnyPlayer;
            return family && SourceName.Length > 0 ? $"{mode} · {SourceName.ToUpperInvariant()}" : mode;
        }
    }

    /// <summary>Select a source (clears a pinned session). Used by every picker.</summary>
    public void SetMode(MediaMode m)
    {
        S.MediaMode = m;
        S.MediaSessionId = "";
        DataStore.MarkDirty();
        Pick();
    }

    public void PinSession(string aumid)
    {
        S.MediaSessionId = S.MediaSessionId == aumid ? "" : aumid;
        DataStore.MarkDirty();
        Pick();
    }

    // ================================================================ app identity

    private static readonly string[] Chromium = { "chrome", "brave", "msedge", "opera", "vivaldi", "arc", "chromium", "yandex", "thorium", "browser" };
    private static readonly string[] Gecko = { "firefox", "librewolf", "waterfox", "floorp", "zen", "mullvadbrowser" };

    /// <summary>
    /// Lower-case app key for a session: from its id when readable ("Brave", "Chrome", "MSEdge",
    /// "Valve.Steam.Client"), otherwise from the window that carries the id (anonymous hashes).
    /// </summary>
    private static string AppKey(string aumid)
    {
        string a = aumid.ToLowerInvariant();
        if (a.Contains("steam") || a.Contains("valve")) return "steam";
        // Win32 players without their own id report their exe ("Spotify.exe") or a full path.
        if (a.EndsWith(".exe")) a = System.IO.Path.GetFileNameWithoutExtension(a);
        foreach (var k in Chromium.Concat(Gecko).Append("spotify"))
            if (k != "browser" && a.Contains(k)) return k == "msedge" ? "msedge" : k;
        if (a.Contains("edge")) return "msedge";
        string? proc = AppIdResolver.ProcessFor(aumid);
        if (proc is null) return a;
        string p = proc.ToLowerInvariant();
        if (p.Contains("steam")) return "steam";
        return p;
    }

    public static string Friendly(string aumid) => AppKey(aumid) switch
    {
        "steam" => "Steam browser",
        "chrome" => "Chrome",
        "brave" => "Brave",
        "msedge" => "Edge",
        "opera" => "Opera",
        "vivaldi" => "Vivaldi",
        "arc" => "Arc",
        "spotify" => "Spotify",
        "firefox" => "Firefox",
        "librewolf" => "LibreWolf",
        "applemusic" or "applemusicwin" or "appleinc.applemusicwin" => "Apple Music",
        "itunes" => "iTunes",
        "zunemusic" or "microsoft.zunemusic" => "Media Player",
        "vlc" => "VLC",
        "foobar2000" => "foobar2000",
        "musicbee" => "MusicBee",
        "aimp" => "AIMP",
        "tidal" => "TIDAL",
        "deezer" => "Deezer",
        "amazon music" or "amazonmusic" => "Amazon Music",
        "youtube music" or "youtubemusic" or "youtube-music-desktop-app" => "YouTube Music",
        "soundcloud" => "SoundCloud",
        "discord" => "Discord",
        var k when k.Length is > 0 and <= 24 && !k.All(char.IsAsciiHexDigit) => char.ToUpperInvariant(k[0]) + k[1..],
        _ => "Unknown player",
    };

    /// <summary>The single-app source for a session, if it has one (for "switch to Brave").</summary>
    private static MediaMode? ModeFor(string aumid) => AppKey(aumid) switch
    {
        "steam" => MediaMode.SteamBrowser,
        "chrome" => MediaMode.Chrome,
        "brave" => MediaMode.Brave,
        "msedge" => MediaMode.Edge,
        "opera" => MediaMode.Opera,
        "vivaldi" => MediaMode.Vivaldi,
        "spotify" => MediaMode.Spotify,
        var k when Gecko.Contains(k) => MediaMode.Firefox,
        var k when Chromium.Contains(k) => MediaMode.ChromeBrave,
        _ => null,
    };

    private static bool Matches(MediaMode mode, string aumid)
    {
        string k = AppKey(aumid);
        return mode switch
        {
            MediaMode.Auto or MediaMode.AnyPlayer => true,
            MediaMode.SteamBrowser => k == "steam",
            MediaMode.ChromeBrave => Chromium.Contains(k),
            MediaMode.Chrome => k == "chrome",
            MediaMode.Brave => k == "brave",
            MediaMode.Edge => k == "msedge",
            MediaMode.Opera => k == "opera",
            MediaMode.Vivaldi => k == "vivaldi",
            MediaMode.Firefox => Gecko.Contains(k),
            MediaMode.Spotify => k == "spotify",
            _ => false,
        };
    }

    private static bool Matches(GlobalSystemMediaTransportControlsSession s) =>
        S.MediaSessionId.Length > 0 ? s.SourceAppUserModelId == S.MediaSessionId : Matches(S.MediaMode, s.SourceAppUserModelId);

    /// <summary>
    /// The Steam overlay browser draws inside the game, so it has no window of its own and Windows may
    /// report it under an anonymous id that can't be traced to a process. When Steam's web helper is
    /// running, an otherwise unidentified player is almost certainly it.
    /// </summary>
    private static bool LikelySteamOverlay(string aumid)
    {
        if (Friendly(aumid) != "Unknown player") return false;
        if ((DateTime.UtcNow - _steamChecked).TotalSeconds > 10)
        {
            _steamChecked = DateTime.UtcNow;
            try { var ps = System.Diagnostics.Process.GetProcessesByName("steamwebhelper"); _steamRunning = ps.Length > 0; foreach (var p in ps) p.Dispose(); }
            catch (Exception) { _steamRunning = false; }
        }
        return _steamRunning;
    }

    private static DateTime _steamChecked = DateTime.MinValue;
    private static bool _steamRunning;

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch (Exception) { return false; }
    }

    // ================================================================ lifecycle

    public async Task StartAsync()
    {
        if (_mgr is not null) { Pick(); return; }
        try
        {
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.SessionsChanged += (_, _) => OnUi(Pick);
            _mgr.CurrentSessionChanged += (_, _) => OnUi(Pick);
            _poll.Tick += (_, _) => Pick();
            _poll.Start();
            Pick();
        }
        catch (Exception ex)
        {
            Status = "Media controls unavailable on this PC";
            Log.Warn("media: " + ex.Message);
            Changed?.Invoke();
        }
    }

    private static void OnUi(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    /// <summary>Every player Windows reports right now (no titles, safe to call from menus).</summary>
    public List<MediaSource> SourcesQuick()
    {
        var list = new List<MediaSource>();
        if (_mgr is null) return list;
        try
        {
            foreach (var s in _mgr.GetSessions())
            {
                string app = Friendly(s.SourceAppUserModelId);
                list.Add(new MediaSource(s.SourceAppUserModelId, app, app, IsPlaying(s)));
            }
        }
        catch (Exception) { }
        return list.OrderByDescending(x => x.Playing).ToList();
    }

    /// <summary>True if a session for this source is playing right now (● in the pickers).</summary>
    public bool IsModePlaying(MediaMode m)
    {
        if (_mgr is null) return false;
        try { return _mgr.GetSessions().Any(s => Matches(m, s.SourceAppUserModelId) && IsPlaying(s)); }
        catch (Exception) { return false; }
    }

    /// <summary>Chooses the session to follow for the selected source (prefers one that is playing).</summary>
    public void Pick()
    {
        if (_mgr is null) return;
        GlobalSystemMediaTransportControlsSession? best = null;
        string elsewhere = "";
        MediaMode? elsewhereMode = null;
        try
        {
            var sessions = _mgr.GetSessions().ToList();
            var mine = sessions.Where(Matches).ToList();
            if (mine.Count == 0 && S.MediaMode == MediaMode.SteamBrowser && S.MediaSessionId.Length == 0)
                mine = sessions.Where(s => LikelySteamOverlay(s.SourceAppUserModelId)).ToList();
            best = mine.FirstOrDefault(IsPlaying);
            if (best is null && S.MediaMode is MediaMode.Auto or MediaMode.AnyPlayer && S.MediaSessionId.Length == 0)
                best = _mgr.GetCurrentSession();
            // Stick with the one we had if it's still around (avoids flipping between two paused tabs).
            best ??= mine.FirstOrDefault(s => ReferenceEquals(s, _session)) ?? mine.FirstOrDefault();

            if (best is null || !IsPlaying(best))
            {
                var other = sessions.FirstOrDefault(s => !mine.Contains(s) && IsPlaying(s));
                if (other is not null) { elsewhere = Friendly(other.SourceAppUserModelId); elsewhereMode = ModeFor(other.SourceAppUserModelId) ?? MediaMode.Auto; }
            }
        }
        catch (Exception ex) { Log.Warn("media pick: " + ex.Message); }

        _elsewhereMode = elsewhereMode;
        PlayingElsewhere = elsewhere;

        if (!ReferenceEquals(best, _session))
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnProps;
                _session.PlaybackInfoChanged -= OnPlayback;
            }
            _session = best;
            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnProps;
                _session.PlaybackInfoChanged += OnPlayback;
            }
            _ = RefreshAsync();
        }
        else if (_session is null || Playing != IsPlaying(_session)) _ = RefreshAsync();
        else RaiseIfChanged();
    }

    private void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => OnUi(() => _ = RefreshAsync());
    private void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => OnUi(() => _ = RefreshAsync());

    private async Task RefreshAsync()
    {
        var s = _session;
        HasSession = s is not null;
        if (s is null)
        {
            Title = "";
            Artist = "";
            Playing = false;
            SourceName = "";
            Status = S.MediaSessionId.Length > 0 ? "The pinned player isn't open"
                : S.MediaMode is MediaMode.Auto or MediaMode.AnyPlayer ? "Nothing playing"
                : $"Nothing playing in {Describe(S.MediaMode)}";
            RaiseIfChanged();
            return;
        }
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            Title = props?.Title ?? "";
            Artist = props?.Artist ?? "";
            // Screenshot mode (website / docs captures): never show what's really playing on this PC.
            if (App.ScreenshotMode) { Title = "Clone Wars Ambience · 501st Mix"; Artist = "Field Ops Radio"; }
            Playing = IsPlaying(s);
            SourceName = S.MediaMode == MediaMode.SteamBrowser && LikelySteamOverlay(s.SourceAppUserModelId) ? "Steam browser" : Friendly(s.SourceAppUserModelId);
            Status = Playing ? "Playing" : "Paused";
        }
        catch (Exception ex) { Status = "Can't read the player"; Log.Warn("media refresh: " + ex.Message); }
        RaiseIfChanged();
    }

    private string _lastSig = "\0";

    /// <summary>
    /// The 2 s poll and chatty players (Chromium raises PlaybackInfoChanged for every timeline tick) used to
    /// rebuild the overlay's player and the home page each time. Now listeners only hear about real changes.
    /// </summary>
    private void RaiseIfChanged()
    {
        string sig = string.Join("\u001f", HasSession, Title, Artist, Playing, SourceName, Status, PlayingElsewhere, ModeLabel);
        if (sig == _lastSig) return;
        _lastSig = sig;
        Raise(nameof(ModeLabel));
        Changed?.Invoke();
    }

    // ================================================================ controls

    /// <summary>The keyboard media key goes to whatever Windows thinks is current, so only use it when that's what was asked for.</summary>
    private static bool MediaKeyOk => S.MediaSessionId.Length == 0 && S.MediaMode is MediaMode.Auto or MediaMode.AnyPlayer;

    public async Task PlayPauseAsync()
    {
        if (_mgr is null) await StartAsync();
        if (_session is null)
        {
            if (MediaKeyOk) SendMediaKey();
            else { Status = $"Nothing to control in {Describe(S.MediaMode)}. Start something there, or pick another source."; Changed?.Invoke(); }
            return;
        }
        try
        {
            bool ok = Playing ? await _session.TryPauseAsync() : await _session.TryPlayAsync();
            if (!ok) ok = await _session.TryTogglePlayPauseAsync();
            if (!ok && MediaKeyOk) SendMediaKey();
        }
        catch (Exception) { if (MediaKeyOk) SendMediaKey(); }
        _ = RefreshAsync();
    }

    // ================================================================ volume (the player app's own mixer slider)

    /// <summary>Process names whose audio is the current player (lower case, no .exe).</summary>
    private static HashSet<string> AudioProcesses(string aumid)
    {
        string k = AppKey(aumid);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        switch (k)
        {
            case "steam": set.Add("steamwebhelper"); set.Add("steam"); break;
            case "msedge": set.Add("msedge"); set.Add("msedgewebview2"); break;
            default: if (k.Length is > 0 and <= 40) set.Add(k); break;
        }
        if (AppIdResolver.ProcessFor(aumid) is { Length: > 0 } p) set.Add(p.ToLowerInvariant());
        return set;
    }

    /// <summary>The player's volume (0..1) in the Windows mixer, or null when there's no player or it has no audio session yet.</summary>
    public double? Volume => _session is null ? null : AppVolume.Get(AudioProcesses(_session.SourceAppUserModelId));

    /// <summary>Sets only the player app's volume (never the game or the master volume).</summary>
    public bool SetVolume(double level) => _session is not null && AppVolume.Set(AudioProcesses(_session.SourceAppUserModelId), level) > 0;

    /// <summary>Steps the player's volume; returns the new level, or null if it couldn't be changed.</summary>
    public double? NudgeVolume(double delta)
    {
        if (Volume is not { } v) return null;
        double nv = Math.Clamp(Math.Round((v + delta) * 50) / 50, 0, 1);
        return SetVolume(nv) ? nv : null;
    }

    public async Task NextAsync() { if (_session is not null) try { await _session.TrySkipNextAsync(); } catch (Exception) { } }
    public async Task PrevAsync() { if (_session is not null) try { await _session.TrySkipPreviousAsync(); } catch (Exception) { } }

    /// <summary>Track progress for the overlay player. Null when the player doesn't report a timeline.</summary>
    public (TimeSpan Position, TimeSpan Length)? Timeline()
    {
        try
        {
            var t = _session?.GetTimelineProperties();
            if (t is null || t.EndTime <= TimeSpan.Zero) return null;
            var pos = t.Position;
            // Windows only updates Position on state changes; extrapolate while playing.
            if (Playing) pos += DateTimeOffset.Now - t.LastUpdatedTime;
            if (pos > t.EndTime) pos = t.EndTime;
            return (pos < TimeSpan.Zero ? TimeSpan.Zero : pos, t.EndTime);
        }
        catch (Exception) { return null; }
    }

    public async Task SeekAsync(TimeSpan to)
    {
        if (_session is null) return;
        try { await _session.TryChangePlaybackPositionAsync(to.Ticks); } catch (Exception) { }
    }

    /// <summary>Fallback: the keyboard's play/pause key, which Windows routes to the active player.</summary>
    private static void SendMediaKey()
    {
        const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
        NativeMethods.keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 0, UIntPtr.Zero);
        NativeMethods.keybd_event(VK_MEDIA_PLAY_PAUSE, 0, 2, UIntPtr.Zero);
    }
}
