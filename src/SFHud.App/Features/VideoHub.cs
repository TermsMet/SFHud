using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SFHud.Services;

/// <summary>
/// One player per Video element, kept across overlay rebuilds so a layout change doesn't restart the
/// video. Plays local files (mp4, wmv, mov, webm with the codec installed…) or a direct http(s) link
/// to a video file through Windows Media Foundation. Streaming pages (YouTube, Twitch) aren't video
/// files; open those in the Steam overlay browser and control them as music instead.
/// </summary>
public sealed class VideoPlayer
{
    public OverlayElement El { get; }
    public MediaElement Media { get; }
    public bool Playing { get; private set; }
    public bool Opened { get; private set; }
    public string Error { get; private set; } = "";
    public string Source { get; private set; } = "";
    /// <summary>Paused because the game lost focus; resumes when it comes back.</summary>
    internal bool AutoPaused;

    internal VideoPlayer(OverlayElement el)
    {
        El = el;
        Media = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Manual, // re-parenting on rebuild must not stop it
            Stretch = System.Windows.Media.Stretch.Uniform,
            ScrubbingEnabled = true,
        };
        Media.MediaOpened += (_, _) => { Opened = true; Error = ""; VideoHub.RaiseChanged(); };
        Media.MediaFailed += (_, e) => { Opened = false; Playing = false; Error = Friendly(e.ErrorException); Log.Warn("video: " + e.ErrorException?.Message); VideoHub.RaiseChanged(); };
        Media.MediaEnded += (_, _) =>
        {
            if (El.VideoLoop) { Media.Position = TimeSpan.Zero; Media.Play(); }
            else { Playing = false; VideoHub.RaiseChanged(); }
        };
        Apply();
    }

    private static string Friendly(Exception? ex) =>
        ex?.Message.Contains("0xC00D11B1", StringComparison.OrdinalIgnoreCase) == true || ex?.Message.Contains("codec", StringComparison.OrdinalIgnoreCase) == true
            ? "This video's format needs a codec Windows doesn't have. MP4 (H.264) always works."
            : "Couldn't play this video" + (ex is null ? "." : ": " + ex.Message);

    /// <summary>Picks up source / loop / volume changes from the element editor.</summary>
    public void Apply()
    {
        Media.IsMuted = El.VideoMuted;
        Media.Volume = Math.Clamp(El.VideoVolume, 0, 1);
        string src = El.VideoSource.Trim();
        if (src == Source) return;
        Source = src;
        Opened = false;
        Error = "";
        if (VideoHub.ToUri(src) is { } uri)
        {
            Media.Source = uri;
            Media.Play();
            Playing = true;
        }
        else
        {
            Media.Source = null;
            Playing = false;
            if (src.Length > 0) Error = "File not found, or not a video link.";
        }
        VideoHub.RaiseChanged();
    }

    public void Play() { if (Media.Source is null) return; Media.Play(); Playing = true; AutoPaused = false; VideoHub.RaiseChanged(); }
    public void Pause() { Media.Pause(); Playing = false; VideoHub.RaiseChanged(); }
    public void Toggle() { if (Playing) Pause(); else Play(); }
    public void Restart() { Media.Position = TimeSpan.Zero; Play(); }

    public void Seek(TimeSpan t)
    {
        if (!Opened) return;
        var len = Length;
        Media.Position = len > TimeSpan.Zero && t > len ? len : t < TimeSpan.Zero ? TimeSpan.Zero : t;
        VideoHub.RaiseChanged();
    }

    public void SetMuted(bool m) { El.VideoMuted = m; Media.IsMuted = m; DataStore.MarkDirty(); VideoHub.RaiseChanged(); }
    public void SetVolume(double v) { El.VideoVolume = Math.Clamp(v, 0, 1); Media.Volume = El.VideoVolume; if (v > 0 && El.VideoMuted) SetMuted(false); DataStore.MarkDirty(); }

    public TimeSpan Position => Media.Position;
    public TimeSpan Length => Opened && Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : TimeSpan.Zero;
    public string Name => El.Name.Length > 0 ? El.Name : El.Title.Length > 0 && El.Title != "VIDEO" ? El.Title : Source.Length > 0 ? Path.GetFileNameWithoutExtension(Source.Split('?')[0]) : "Video";

    internal void Close()
    {
        try { Media.Stop(); Media.Close(); } catch (Exception) { }
        Detach();
    }

    /// <summary>Takes the MediaElement out of whatever it was shown in, so a rebuilt element can host it.</summary>
    public void Detach()
    {
        switch (Media.Parent)
        {
            case Decorator d: d.Child = null; break;
            case Panel p: p.Children.Remove(Media); break;
            case ContentControl c: c.Content = null; break;
        }
    }
}

public static class VideoHub
{
    private static readonly Dictionary<string, VideoPlayer> Players = new();
    private static readonly DispatcherTimer Cleanup = new() { Interval = TimeSpan.FromSeconds(5) };

    public static event Action? Changed;
    internal static void RaiseChanged() => Changed?.Invoke();

    public static readonly string[] Extensions = { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mpg", ".mpeg", ".3gp" };
    public const string FileFilter = "Videos (*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg)|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm;*.mpg;*.mpeg;*.3gp|All files (*.*)|*.*";

    public static Uri? ToUri(string src)
    {
        if (src.Length == 0) return null;
        if (Uri.TryCreate(src, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) return u;
        try { return File.Exists(src) ? new Uri(Path.GetFullPath(src)) : null; }
        catch (Exception) { return null; }
    }

    public static VideoPlayer Get(OverlayElement el)
    {
        if (!Players.TryGetValue(el.Id, out var p) || !ReferenceEquals(p.El, el))
        {
            p?.Close();
            p = new VideoPlayer(el);
            Players[el.Id] = p;
            if (!Cleanup.IsEnabled) { Cleanup.Tick += (_, _) => Prune(); Cleanup.Start(); }
        }
        else p.Apply();
        return p;
    }

    /// <summary>The player the pop-up controls: the playing one, else the last one with a video.</summary>
    public static VideoPlayer? Current =>
        Players.Values.Where(p => Exists(p.El)).OrderByDescending(p => p.Playing).ThenByDescending(p => p.Source.Length > 0).FirstOrDefault(p => p.Source.Length > 0);

    public static IEnumerable<VideoPlayer> All => Players.Values.Where(p => Exists(p.El));

    private static bool Exists(OverlayElement el) => DataStore.Preset.ActivePage.Elements.Contains(el);

    /// <summary>Stops players whose element was deleted or whose page isn't showing.</summary>
    public static void Prune()
    {
        foreach (var (id, p) in Players.ToList())
            if (!Exists(p.El)) { p.Close(); Players.Remove(id); RaiseChanged(); }
    }

    /// <summary>Called when the game loses / regains focus (if the user wants videos to pause with it).</summary>
    public static void GameFocus(bool inGame)
    {
        if (!DataStore.Settings.VideoPauseOutOfGame) return;
        foreach (var p in All)
        {
            if (!inGame && p.Playing) { p.Pause(); p.AutoPaused = true; }
            else if (inGame && p.AutoPaused) p.Play();
        }
    }

    public static void StopAll()
    {
        foreach (var p in Players.Values) p.Close();
        Players.Clear();
        RaiseChanged();
    }
}
