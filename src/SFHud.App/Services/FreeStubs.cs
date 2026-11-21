// SFHud Free: empty stand-ins for the Pro features. The real code (local AI, video, screen reading,
// companion window, chat alerts) lives in the Pro\ folder, which is not part of the free
// build or the open-source repo. So the Free exe has nothing to "unlock": these types only exist so the
// shared code compiles, every call to them is behind an Edition.Has(...) check, and they do nothing.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SFHud.Services
{
    public enum AiState { NotInstalled, Installing, Stopped, Starting, Ready, Busy, Error }

    /// <summary>Free: no local AI.</summary>
    public sealed class LocalAi : Observable
    {
        public static LocalAi Instance { get; } = new();
        public string StatusText => "Part of SFHud Pro";
        public string LastError => "The local AI is part of SFHud Pro.";
        public bool CanRun => false;
        public bool IsRunning => false;
        public bool HeldUnloaded => false;
        public bool CanAutoUse => false;
        public void Unload() { }
        public Task<bool> LoadAsync(CancellationToken ct = default) => Task.FromResult(false);
        public void Preload() { }
        public void RefreshInstalled() { }
        public void Shutdown() { }
        public Task<string?> ChatAsync(string system, string user, int maxTokens = 0, bool json = false, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public static JsonNode? ParseJsonLoose(string? reply) => null;
    }

    /// <summary>Free: no screen reading.</summary>
    public static class OcrService
    {
        public static bool Available => false;
        public static Task<string?> ReadRegionAsync(IntPtr game, double fx, double fy, double fw, double fh) => Task.FromResult<string?>(null);
    }

    /// <summary>Free: no video playback.</summary>
    public sealed class VideoPlayer
    {
        private VideoPlayer() { }
        public OverlayElement El => throw new NotSupportedException();
        public bool Playing => false;
        public void Toggle() { }
        public void Restart() { }
    }

    public static class VideoHub
    {
        public const string FileFilter = "Videos (*.mp4)|*.mp4";
        public static event Action? Changed { add { } remove { } }
        public static IEnumerable<VideoPlayer> All => Enumerable.Empty<VideoPlayer>();
        public static VideoPlayer? Current => null;
        public static void GameFocus(bool inGame) { }
        public static void StopAll() { }
    }

    /// <summary>Free: chat alerts are not included.</summary>
    public static class ChatWatch
    {
        public static event Action<string, string, bool, string>? Alert { add { } remove { } }
        public static event Action<string, int>? CountdownRequested { add { } remove { } }
        public static void Start() { }
    }
}

namespace SFHud.Views
{
    /// <summary>Free: never created (the companion window is Pro).</summary>
    public sealed class CompanionWindow : Window { }

    /// <summary>Free: never created (the AI menu is Pro).</summary>
    public sealed class AiWindow : Window
    {
        public AiWindow(SFHud.Services.GameTarget target) { }
    }
}
