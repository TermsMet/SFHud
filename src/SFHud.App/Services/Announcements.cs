using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SFHud.Services;

/// <summary>
/// Update check against the public GitHub releases (github.com/TermsMet/SFHud). The newest release
/// shows once in the update bar when it's newer than this copy. "Update now" downloads its installer,
/// checks its SHA-256 against the one published with the release, and runs setup (which keeps all your data).
/// </summary>
public sealed class Announcements
{
    public static Announcements Instance { get; } = new();

    public sealed record Item(string Id, string Kind, string Title, string Body, string Version)
    {
        public bool IsUpdate => Kind == "update";
    }

    private const string Repo = "TermsMet/SFHud";

#if DEBUG
    // Debug builds only: SFHUD_UPDATES_URL points the check at a test server (GitHub-style /repos/.../releases/latest).
    private static string ApiHost => Environment.GetEnvironmentVariable("SFHUD_UPDATES_URL") is { Length: > 0 } u ? u : "https://api.github.com";
#else
    private static string ApiHost => "https://api.github.com";
#endif

    private static readonly HttpClient Http = MakeClient(TimeSpan.FromSeconds(15));
    private static readonly HttpClient Download = MakeClient(TimeSpan.FromMinutes(10));

    private static HttpClient MakeClient(TimeSpan timeout)
    {
        var c = new HttpClient { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"SFHud/{AppInfo.Version}");
        return c;
    }

    public IReadOnlyList<Item> Items { get; private set; } = Array.Empty<Item>();
    private string _installerSha = "", _installerUrl = "", _shaUrl = "";
    public event Action? Changed;

    private Announcements() { }

    /// <summary>Reads the latest release. Never throws.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var j = await Http.GetFromJsonAsync<JsonNode>($"{ApiHost}/repos/{Repo}/releases/latest");
            if (j is null || j["draft"]?.GetValue<bool>() == true || j["prerelease"]?.GetValue<bool>() == true) return;
            string tag = j["tag_name"]?.GetValue<string>() ?? "";
            string version = tag.TrimStart('v', 'V');
            if (!System.Version.TryParse(version, out _)) return;

            string sha = "", url = "", shaUrl = "";
            if (j["assets"] is JsonArray assets)
                foreach (var a in assets)
                {
                    string name = a?["name"]?.GetValue<string>() ?? "";
                    string dl = a?["browser_download_url"]?.GetValue<string>() ?? "";
                    // Installer: SFHud-Setup-<version>.exe (older releases: SFHud-Free-Setup-<version>.exe).
                    if (name.EndsWith($"Setup-{version}.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        url = dl;
                        string digest = a?["digest"]?.GetValue<string>() ?? "";
                        if (digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) sha = digest[7..].ToLowerInvariant();
                    }
                    else if (name.EndsWith($"Setup-{version}.exe.sha256", StringComparison.OrdinalIgnoreCase)) shaUrl = dl;
                }

            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("gh:" + tag)))[..16].ToLowerInvariant();
            Items = new[] { new Item(id, "update", Clip(j["name"]?.GetValue<string>() ?? $"SFHud {version}", 120), Clip(j["body"]?.GetValue<string>(), 4000), version) };
            _installerSha = sha;
            _installerUrl = url;
            _shaUrl = shaUrl;
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Info("update check: " + ex.Message); }
    }

    /// <summary>Posts not shown yet, oldest first. Updates for a version you already have are skipped.</summary>
    public IEnumerable<Item> Pending(ICollection<string> seen) =>
        Items.Where(i => !seen.Contains(i.Id) && (!i.IsUpdate || IsNewer(i.Version, AppInfo.Version))).Reverse();

    /// <summary>The newest update above the version you run (for the update bar), or null.</summary>
    public Item? Latest => Items.Where(i => i.IsUpdate && IsNewer(i.Version, AppInfo.Version))
        .OrderByDescending(i => System.Version.Parse(i.Version)).FirstOrDefault();

    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(candidate, out var c) && System.Version.TryParse(current, out var cur) && c > cur;

    /// <summary>Downloads and starts the new installer. Returns "" when setup started, otherwise a message.
    /// <paramref name="unused"/> is left over from the old paid edition and ignored.</summary>
    public async Task<string> InstallUpdateAsync(string version, Func<Task<(string Url, string Error)>>? unused, Action<string>? progress = null)
    {
        if (_installerUrl.Length == 0) return "The update has no installer attached yet. Try again later.";
        string expected = _installerSha;
        if (expected.Length != 64 && _shaUrl.Length > 0)
        {
            try { expected = ((await Http.GetStringAsync(_shaUrl)).Trim().Split(' ', '\t', '*')[0]).ToLowerInvariant(); }
            catch (Exception) { expected = ""; }
        }
        if (expected.Length != 64) return "The update's checksum isn't published yet. Try again later.";

        string dir = Path.Combine(Path.GetTempPath(), "SFHud-Update");
        Directory.CreateDirectory(dir);
        // "Setup" in the name makes the exe start in setup mode; setup closes SFHud (it saves first).
        string file = Path.Combine(dir, $"SFHud-Setup-{version}.exe");
        try
        {
            progress?.Invoke("Downloading the update…");
            using (var resp = await Download.GetAsync(new Uri(_installerUrl), HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) return $"The download failed ({(int)resp.StatusCode}). Try again.";
                await using var fs = File.Create(file);
                await resp.Content.CopyToAsync(fs);
            }
            progress?.Invoke("Checking the download…");
            string sha;
            await using (var fs = File.OpenRead(file)) sha = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
            if (sha != expected)
            {
                try { File.Delete(file); } catch (Exception) { }
                return "The download didn't match the published checksum, so it wasn't run. Try again later.";
            }
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = dir });
            return "";
        }
        catch (Exception ex)
        {
            Log.Warn("update: " + ex.Message);
            return "The update couldn't be downloaded. Check your internet connection.";
        }
    }

    private static string Clip(string? s, int max) { s = (s ?? "").Trim(); return s.Length > max ? s[..max] : s; }
}
