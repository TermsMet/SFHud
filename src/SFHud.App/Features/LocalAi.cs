using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using SFHud.Interop;

namespace SFHud.Services;

public enum AiState { NotInstalled, Installing, Stopped, Starting, Ready, Busy, Error }

/// <summary>A pinned download with a known size and SHA-256.</summary>
public sealed record AiAsset(string Name, string Url, string Sha256, long Size, string FileName);

/// <summary>
/// Optional local model: Qwen3.5-0.8B (Q8_0 GGUF, ~812 MB) on a pinned llama.cpp CPU build.
/// The server listens on 127.0.0.1 with a random port and a per-launch API key, loads on first use,
/// runs below-normal priority on a few threads so the game keeps its frame rate, and unloads when idle.
/// Nothing in SFHud depends on it.
/// </summary>
public sealed partial class LocalAi : Observable
{
    private static LocalAi? _instance;
    /// <summary>Lazy, so the pinned assets below are initialized first.</summary>
    public static LocalAi Instance => _instance ??= new();

    public const string RuntimeTag = "b12416";

    public static readonly AiAsset Runtime = new(
        "llama.cpp b12416 · CPU x64",
        $"https://github.com/ggml-org/llama.cpp/releases/download/{RuntimeTag}/llama-{RuntimeTag}-bin-win-cpu-x64.zip",
        "ba5e0145484ec11ba8a1beac9aafa591b074ff112e7b98afa712f6633b18b76c",
        25_142_726,
        "llama-cpu.zip");

    public static readonly AiAsset Model = new(
        "Qwen3.5-0.8B · Q8_0 GGUF",
        "https://huggingface.co/unsloth/Qwen3.5-0.8B-GGUF/resolve/main/Qwen3.5-0.8B-Q8_0.gguf",
        "0ad885ffd4bb022fc4f0d33a3308fa108ef8613159d3b3a67e23abca056b7a6c",
        811_843_840,
        "Qwen3.5-0.8B-Q8_0.gguf");

    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(30) };
    private Process? _server;
    private string _baseUrl = "";
    private string _apiKey = "";
    private string _modelName = "local";
    private DateTime _lastUse = DateTime.UtcNow;
    private CancellationTokenSource? _installCts;

    private AiState _state;
    private string _statusText = "";
    private double _progress;
    private string _progressText = "";
    private string _lastError = "";

    private LocalAi()
    {
        _state = IsInstalled ? AiState.Stopped : AiState.NotInstalled;
        _statusText = DescribeState(_state);
        _idle.Tick += (_, _) =>
        {
            if (_server is not null && State == AiState.Ready && DateTime.UtcNow - _lastUse > TimeSpan.FromMinutes(Math.Clamp(DataStore.Settings.AiIdleMinutes, 1, 120))) Stop("UNLOADED · IDLE");
        };
        _idle.Start();
    }

    public AiState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            StatusText = DescribeState(value);
            Raise(nameof(IsInstalled));
            Raise(nameof(CanRun));
            StateChanged?.Invoke();
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }
    public string LastError { get => _lastError; private set { if (Set(ref _lastError, value) && value.Length > 0) Log.Warn("ai: " + value); } }

    /// <summary>Last lines llama-server printed, so a crash can say why.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _serverLog = new();
    private string ServerTail() => string.Join(" | ", _serverLog.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("failed", StringComparison.OrdinalIgnoreCase)).TakeLast(2));

    public event Action? StateChanged;

    public static string ModelPath => Path.Combine(SettingsStore.ModelsDir, Model.FileName);
    public static string RuntimePath => Path.Combine(SettingsStore.RuntimeDir, "llama-" + RuntimeTag);
    public static string ServerExe => Path.Combine(RuntimePath, "llama-server.exe");

    public bool IsInstalled => File.Exists(ServerExe) && File.Exists(ModelPath);
    public bool UsingExternal => ExternalBase() is not null;
    /// <summary>Local AI is an SFHud Pro feature.</summary>
    public bool CanRun => Edition.Has(ProFeature.LocalAi) && (UsingExternal || IsInstalled);
    public bool IsRunning => State is AiState.Ready or AiState.Busy;

    /// <summary>Benchmarked: prompt speed peaks around 8 threads; text generation is memory-bound, so more threads only cost the game frames.</summary>
    public static int AutoThreads => Math.Clamp(Environment.ProcessorCount / 4, Math.Min(4, Environment.ProcessorCount), 8);

    /// <summary>
    /// True after you click UNLOAD: the model stays unloaded. Opening the AI page or window, DEFCON auto-switch
    /// and chat event alerts don't load it again; only LOAD or a question you ask yourself does.
    /// </summary>
    public bool HeldUnloaded { get; private set; }

    /// <summary>The AI may be used by background features (DEFCON auto-switch, chat alerts) without asking you.</summary>
    public bool CanAutoUse => CanRun && !HeldUnloaded;

    /// <summary>UNLOAD: frees the memory and keeps it free until you load it again.</summary>
    public void Unload() { HeldUnloaded = true; Stop("UNLOADED · stays off until you load it or ask something"); }

    /// <summary>LOAD: clears the hold and loads the model now.</summary>
    public Task<bool> LoadAsync(CancellationToken ct = default) { HeldUnloaded = false; return EnsureRunningAsync(ct); }

    /// <summary>Starts loading the model in the background (AI window or page opened), so the first answer doesn't wait for it. Not after UNLOAD.</summary>
    public void Preload() { if (CanRun && !HeldUnloaded && !IsRunning && State != AiState.Starting) _ = EnsureRunningAsync(); }

    public string BackendText => UsingExternal
        ? $"EXTERNAL · {DataStore.Settings.AiEndpoint}"
        : $"{Model.Name}  ·  {Runtime.Name}  ·  {(DataStore.Settings.AiThreads > 0 ? DataStore.Settings.AiThreads : AutoThreads)} THREADS{(DataStore.Settings.AiThreads > 0 ? "" : " (AUTO)")}";

    private static string DescribeState(AiState s) => s switch
    {
        AiState.NotInstalled => "NOT INSTALLED",
        AiState.Installing => "DOWNLOADING",
        AiState.Stopped => "READY TO LOAD",
        AiState.Starting => "LOADING MODEL",
        AiState.Ready => "ONLINE",
        AiState.Busy => "THINKING",
        _ => "ERROR",
    };

    public void RefreshInstalled()
    {
        if (State is AiState.NotInstalled or AiState.Stopped or AiState.Error && _server is null)
            State = CanRun ? AiState.Stopped : AiState.NotInstalled;
        Raise(nameof(BackendText));
    }

    // ================================================================ install

    public async Task InstallAsync()
    {
        if (State == AiState.Installing) return;
        _installCts = new CancellationTokenSource();
        var ct = _installCts.Token;
        State = AiState.Installing;
        LastError = "";
        try
        {
            SettingsStore.EnsureFolders();
            Directory.CreateDirectory(SettingsStore.RuntimeDir);

            if (!File.Exists(ServerExe))
            {
                string zip = Path.Combine(SettingsStore.RuntimeDir, Runtime.FileName);
                await DownloadVerifiedAsync(Runtime, zip, 0, 0.03, ct);
                ProgressText = "UNPACKING RUNTIME…";
                string staging = RuntimePath + ".staging";
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                await Task.Run(() => ZipFile.ExtractToDirectory(zip, staging), ct);
                // Some releases nest everything in a folder; flatten to the folder holding llama-server.exe.
                string? exe = Directory.GetFiles(staging, "llama-server.exe", SearchOption.AllDirectories).FirstOrDefault()
                              ?? throw new InvalidOperationException("llama-server.exe was not in the runtime archive.");
                if (Directory.Exists(RuntimePath)) Directory.Delete(RuntimePath, true);
                Directory.Move(Path.GetDirectoryName(exe)!, RuntimePath);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                File.Delete(zip);
            }

            if (!File.Exists(ModelPath))
                await DownloadVerifiedAsync(Model, ModelPath, 0.03, 0.97, ct);

            Progress = 1;
            ProgressText = "INSTALLED · VERIFIED";
            State = AiState.Stopped;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "PAUSED · PRESS INSTALL TO RESUME";
            State = AiState.NotInstalled;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            ProgressText = "FAILED · " + ex.Message;
            State = AiState.NotInstalled;
        }
    }

    public void CancelInstall() => _installCts?.Cancel();

    /// <summary>Resumable download into "file.part", checked against the pinned SHA-256 before it is renamed into place.</summary>
    private async Task DownloadVerifiedAsync(AiAsset asset, string dest, double from, double span, CancellationToken ct)
    {
        string part = dest + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > asset.Size) { File.Delete(part); have = 0; }

        if (have < asset.Size)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            req.Headers.UserAgent.ParseAdd("SFHud/0.3");
            if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent) have = 0; // server ignored the range

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
            var buffer = new byte[1 << 16];
            var sw = Stopwatch.StartNew();
            long startHave = have;
            DateTime lastUi = DateTime.MinValue;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                have += read;
                if ((DateTime.UtcNow - lastUi).TotalMilliseconds > 150)
                {
                    lastUi = DateTime.UtcNow;
                    double mbps = (have - startHave) / 1048576.0 / Math.Max(0.1, sw.Elapsed.TotalSeconds);
                    Progress = from + span * have / asset.Size;
                    ProgressText = $"{asset.Name.ToUpperInvariant()}  ·  {have / 1048576.0:0} / {asset.Size / 1048576.0:0} MB  ·  {mbps:0.0} MB/s";
                }
            }
        }

        ProgressText = $"VERIFYING {asset.Name.ToUpperInvariant()}…";
        string hash = await Task.Run(() =>
        {
            using var fs = File.OpenRead(part);
            return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
        }, ct);
        if (hash != asset.Sha256)
        {
            File.Delete(part);
            throw new InvalidOperationException($"{asset.Name} failed its checksum and was deleted. Try again.");
        }
        File.Move(part, dest, overwrite: true);
    }

    public void Uninstall()
    {
        Stop();
        try { if (File.Exists(ModelPath)) File.Delete(ModelPath); } catch (Exception) { }
        try { if (Directory.Exists(RuntimePath)) Directory.Delete(RuntimePath, true); } catch (Exception) { }
        Progress = 0;
        ProgressText = "";
        RefreshInstalled();
    }

    // ================================================================ server lifetime

    /// <summary>Loopback-only OpenAI-compatible base URL from settings, or null.</summary>
    private static string? ExternalBase()
    {
        string e = DataStore.Settings.AiEndpoint.Trim();
        if (e.Length == 0) return null;
        if (!e.Contains("://")) e = "http://" + e;
        if (!Uri.TryCreate(e, UriKind.Absolute, out var uri)) return null;
        bool loopback = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (!loopback) return null; // game chat never leaves this PC
        string b = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? b[..^3] : b;
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        _lastUse = DateTime.UtcNow;
        if (IsRunning) return true;

        if (ExternalBase() is { } ext)
        {
            _baseUrl = ext;
            _apiKey = "";
            _modelName = DataStore.Settings.AiExternalModel.Trim();
            try
            {
                State = AiState.Starting;
                if (_modelName.Length == 0)
                {
                    using var r = await _http.GetAsync(_baseUrl + "/v1/models", ct);
                    var j = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct));
                    _modelName = j?["data"]?.AsArray().Select(m => m?["id"]?.GetValue<string>()).FirstOrDefault(id => id is not null && !id.Contains("embed")) ?? "local";
                }
                State = AiState.Ready;
                return true;
            }
            catch (Exception ex)
            {
                LastError = "External server not reachable: " + ex.Message;
                State = AiState.Error;
                return false;
            }
        }

        if (!IsInstalled) { State = AiState.NotInstalled; return false; }
        if (State == AiState.Starting)
        {
            // Another caller is already loading it.
            for (int i = 0; i < 120 && State == AiState.Starting; i++) await Task.Delay(250, ct);
            return IsRunning;
        }

        State = AiState.Starting;
        LastError = "";
        try
        {
            int port = FreePort();
            _apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _baseUrl = $"http://127.0.0.1:{port}";
            _modelName = "local";
            int threads = Math.Clamp(DataStore.Settings.AiThreads > 0 ? DataStore.Settings.AiThreads : AutoThreads, 1, Environment.ProcessorCount);

            var psi = new ProcessStartInfo(ServerExe)
            {
                WorkingDirectory = RuntimePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[]
            {
                "-m", ModelPath, "--host", "127.0.0.1", "--port", port.ToString(), "--api-key", _apiKey,
                "-t", threads.ToString(), "-tb", threads.ToString(), "-c", "4096", "-np", "1", "-ngl", "0", "--no-webui", "-fa", "on",
            }) psi.ArgumentList.Add(a);

            var p = Process.Start(psi) ?? throw new InvalidOperationException("llama-server did not start.");
            NativeMethods.KillWithParent(p);
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            _serverLog.Clear();
            void Keep(string? l) { if (string.IsNullOrWhiteSpace(l)) return; _serverLog.Enqueue(l.Trim()); while (_serverLog.Count > 40) _serverLog.TryDequeue(out _); }
            p.OutputDataReceived += (_, e) => Keep(e.Data);
            p.ErrorDataReceived += (_, e) => Keep(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_server == p) { _server = null; if (State != AiState.Stopped) { string why = ServerTail(); LastError = "The model server stopped" + (why.Length > 0 ? ": " + why : ". Press LOAD MODEL to restart it."); State = AiState.Error; } }
            });
            _server = p;

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                ct.ThrowIfCancellationRequested();
                if (p.HasExited) { string why = ServerTail(); throw new InvalidOperationException($"llama-server exited with code {p.ExitCode}" + (why.Length > 0 ? ": " + why : ". Your antivirus may have blocked it, or the model file is damaged (REMOVE and INSTALL again).")); }
                try
                {
                    using var r = await _http.GetAsync(_baseUrl + "/health", ct);
                    if (r.IsSuccessStatusCode) { State = AiState.Ready; return true; }
                }
                catch (HttpRequestException) { }
                await Task.Delay(250, ct);
            }
            throw new TimeoutException("The model took too long to load.");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            KillServer();
            State = AiState.Error;
            return false;
        }
    }

    public void Stop(string? note = null)
    {
        KillServer();
        State = CanRun ? AiState.Stopped : AiState.NotInstalled;
        if (note is not null) StatusText = note;
    }

    private void KillServer()
    {
        var p = _server;
        _server = null;
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (Exception) { }
        p.Dispose();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ================================================================ chat

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkRx();

    /// <summary>One chat completion. Returns null (and sets <see cref="LastError"/>) on failure.</summary>
    public async Task<string?> ChatAsync(string system, string user, int maxTokens = 0, bool json = false, CancellationToken ct = default)
    {
        // Background features check CanAutoUse first, so a call that gets here while held is something you asked.
        HeldUnloaded = false;
        if (!await EnsureRunningAsync(ct)) return null;
        await _gate.WaitAsync(ct);
        try
        {
            State = AiState.Busy;
            _lastUse = DateTime.UtcNow;
            var body = new JsonObject
            {
                ["model"] = _modelName,
                ["temperature"] = json ? 0.1 : Math.Clamp(DataStore.Settings.AiTemperature, 0, 1.5),
                ["max_tokens"] = maxTokens > 0 ? maxTokens : Math.Clamp(DataStore.Settings.AiMaxTokens, 40, 1200),
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = user },
                },
                // Qwen3.5 thinks out loud by default; SFHud wants short, direct answers.
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            };
            if (json) body["response_format"] = new JsonObject { ["type"] = "json_object" };

            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (_apiKey.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await _http.SendAsync(req, ct);
            string text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Trim(text, 160)}");

            string? content = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
            _lastUse = DateTime.UtcNow;
            if (content is null) return null;
            content = ThinkRx().Replace(content, "");
            // A reply cut off mid-thought has an opening tag and no closing one.
            int open = content.IndexOf("<think>", StringComparison.Ordinal);
            if (open >= 0) content = content[..open];
            int close = content.LastIndexOf("</think>", StringComparison.Ordinal);
            if (close >= 0) content = content[(close + 8)..];
            content = content.Trim();
            if (content.Length == 0) LastError = "The model returned an empty answer. Try a shorter question or raise Max answer length.";
            return content.Length == 0 ? null : content;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { LastError = "The model took too long to answer. Try a shorter request, or lower Max answer length."; return null; }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return null;
        }
        finally
        {
            if (State == AiState.Busy) State = AiState.Ready;
            _gate.Release();
        }
    }

    /// <summary>Pulls the first {...} object out of a reply, for models that wrap JSON in prose or fences.</summary>
    public static JsonNode? ParseJsonLoose(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        int a = reply.IndexOf('{'), b = reply.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try { return JsonNode.Parse(reply[a..(b + 1)]); }
        catch (JsonException) { return null; }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    public void Shutdown()
    {
        _installCts?.Cancel();
        KillServer();
    }
}
