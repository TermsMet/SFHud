using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace SFHud.Services;

/// <summary>
/// Update and news pop-ups posted on the SFHud website (admin › Updates). Each one shows once. An update
/// only shows to people on an older version; "Update now" downloads the installer for this edition, checks
/// its SHA-256 against the one the site published, and runs setup (which keeps all your data).
/// </summary>
public sealed class Announcements
{
    public static Announcements Instance { get; } = new();

    public sealed record Item(string Id, string Kind, string Title, string Body, string Version)
    {
        public bool IsUpdate => Kind == "update";
    }

    private static readonly HttpClient Http = new() { BaseAddress = new Uri(UpdatesHost), Timeout = TimeSpan.FromSeconds(15) };

#if DEBUG
    // Debug builds only: SFHUD_UPDATES_URL points the check at a test server (to try the update bar).
    private static string UpdatesHost => Environment.GetEnvironmentVariable("SFHUD_UPDATES_URL") is { Length: > 0 } u ? u : Edition.SiteUrl;
#else
    private static string UpdatesHost => Edition.SiteUrl;
#endif
    private static readonly HttpClient Download = new() { Timeout = TimeSpan.FromMinutes(10) };

    public IReadOnlyList<Item> Items { get; private set; } = Array.Empty<Item>();
    private string _installerSha = "", _installerUrl = "";
    public event Action? Changed;

    private Announcements() { }

    private static string EditionName => Edition.IsProBuild ? "pro" : "free";

    /// <summary>Fetches the posts for this edition. Never throws.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            var j = await Http.GetFromJsonAsync<JsonNode>($"/api/v1/announcements?edition={EditionName}");
            var list = new List<Item>();
            if (j?["items"] is JsonArray a)
                foreach (var n in a)
                {
                    if (n is null) continue;
                    string id = n["id"]?.GetValue<string>() ?? "";
                    if (id.Length != 16) continue;
                    list.Add(new Item(id, n["kind"]?.GetValue<string>() ?? "news", Clip(n["title"]?.GetValue<string>(), 120),
                        Clip(n["body"]?.GetValue<string>(), 4000), n["version"]?.GetValue<string>() ?? ""));
                }
            _installerSha = j?["installer"]?["sha256"]?.GetValue<string>() ?? "";
            _installerUrl = j?["installer"]?["url"]?.GetValue<string>() ?? "";
            Items = list;
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Info("announcements: " + ex.Message); }
    }

    /// <summary>Posts not shown yet, oldest first. Updates for a version you already have are skipped.</summary>
    public IEnumerable<Item> Pending(ICollection<string> seen) =>
        Items.Where(i => !seen.Contains(i.Id) && (!i.IsUpdate || IsNewer(i.Version, AppInfo.Version))).Reverse();

    /// <summary>The newest update post above the version you run (for the update bar), or null.</summary>
    public Item? Latest => Items.Where(i => i.IsUpdate && IsNewer(i.Version, AppInfo.Version))
        .OrderByDescending(i => Version.Parse(i.Version)).FirstOrDefault();

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var c) && Version.TryParse(current, out var cur) && c > cur;

    /// <summary>
    /// Downloads and starts the new installer. <paramref name="proDownloadUrl"/> gives the Pro build a one-time
    /// link for its license key. Returns "" when setup started, otherwise a message.
    /// </summary>
    public async Task<string> InstallUpdateAsync(string version, Func<Task<(string Url, string Error)>>? proDownloadUrl, Action<string>? progress = null)
    {
        if (_installerSha.Length != 64) return "The update isn't ready on the website yet. Try again later.";
        string url;
        bool pro = Edition.IsProBuild; // a variable, so each edition compiles without "unreachable code"
        if (pro)
        {
            if (proDownloadUrl is null) return "Activate SFHud Pro first, then update.";
            var (u, err) = await proDownloadUrl();
            if (u.Length == 0) return err;
            url = u;
        }
        else url = Edition.SiteUrl + (_installerUrl.Length > 0 ? _installerUrl : "/download/free");

        string dir = Path.Combine(Path.GetTempPath(), "SFHud-Update");
        Directory.CreateDirectory(dir);
        // "Setup" in the name makes the exe start in setup mode; setup closes SFHud (it saves first).
        string file = Path.Combine(dir, $"SFHud-{(Edition.IsProBuild ? "Pro" : "Free")}-Setup-{version}.exe");
        try
        {
            progress?.Invoke("Downloading the update…");
            using (var resp = await Download.GetAsync(new Uri(url), HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) return $"The download failed ({(int)resp.StatusCode}). Try again.";
                await using var fs = File.Create(file);
                await resp.Content.CopyToAsync(fs);
            }
            progress?.Invoke("Checking the download…");
            string sha;
            await using (var fs = File.OpenRead(file)) sha = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();
            if (sha != _installerSha)
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
