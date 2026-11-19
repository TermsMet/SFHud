using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SFHud.Services;

/// <summary>
/// Free Pro trials posted by the SFHud admin on the website. They're only shown to people who have never
/// had Pro on this PC. Checking sends nothing about you; claiming sends a salted hash of this PC's id (never
/// the id itself) so each PC can claim an offer once.
/// </summary>
public sealed class TrialOffers
{
    public static TrialOffers Instance { get; } = new();

    public sealed record Offer(string Id, string Title, string Message, int Days, DateTime EndsUtc);

    private static readonly HttpClient Http = new() { BaseAddress = new Uri(Edition.SiteUrl), Timeout = TimeSpan.FromSeconds(10) };

    public IReadOnlyList<Offer> Offers { get; private set; } = Array.Empty<Offer>();
    public event Action? Changed;
    private DateTime _lastFetch = DateTime.MinValue;

    private TrialOffers() { }

    /// <summary>True once Pro has ever been unlocked on this PC (then offers are never shown again).</summary>
    public static bool EverHadPro
    {
        get { try { return File.Exists(MarkerPath); } catch (Exception) { return false; } }
    }

    /// <summary>Called whenever the edition changes; remembers that this PC has had Pro.</summary>
    public static void NoteEdition()
    {
        if (!Edition.Unlocked || EverHadPro) return;
        try { Directory.CreateDirectory(SettingsStore.DataDir); File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o")); } catch (Exception) { }
    }

    private static string MarkerPath => Path.Combine(SettingsStore.DataDir, "pro.seen");

    /// <summary>Whether offers apply to this user at all.</summary>
    public static bool Eligible => !Edition.Unlocked && !EverHadPro;

    /// <summary>Fetches the live offers (at most every 30 minutes unless forced). Never throws.</summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (!Eligible) { if (Offers.Count > 0) { Offers = Array.Empty<Offer>(); Changed?.Invoke(); } return; }
        if (!force && DateTime.UtcNow - _lastFetch < TimeSpan.FromMinutes(30)) return;
        _lastFetch = DateTime.UtcNow;
        try
        {
            var j = await Http.GetFromJsonAsync<JsonNode>("/api/v1/trials");
            var list = new List<Offer>();
            if (j?["offers"] is JsonArray a)
                foreach (var o in a)
                {
                    if (o is null) continue;
                    string id = o["id"]?.GetValue<string>() ?? "";
                    if (id.Length != 16) continue;
                    list.Add(new Offer(id, Clip(o["title"]?.GetValue<string>(), 80), Clip(o["message"]?.GetValue<string>(), 500),
                        Math.Clamp(o["days"]?.GetValue<int>() ?? 7, 1, 90), o["endsAt"]?.GetValue<DateTime>().ToUniversalTime() ?? DateTime.UtcNow.AddDays(1)));
                }
            Offers = list;
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Info("trial offers: " + ex.Message); }
    }

    /// <summary>Claims an offer: (key, "") or ("", message).</summary>
    public async Task<(string Key, string Error)> ClaimAsync(Offer o)
    {
        try
        {
            using var r = await Http.PostAsJsonAsync("/api/v1/trials/claim", new { offerId = o.Id, machine = MachineHash() });
            var j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
            if (r.IsSuccessStatusCode && j?["key"]?.GetValue<string>() is { Length: > 0 } key) return (key, "");
            return ("", j?["message"]?.GetValue<string>() ?? $"The trial couldn't be claimed ({(int)r.StatusCode}).");
        }
        catch (Exception) { return ("", "Couldn't reach the SFHud website. Check your internet connection."); }
    }

    /// <summary>Asks the website for a one-time Pro add-on download link for a key: (url, "") or ("", message).</summary>
    public static async Task<(string Url, string Error)> ProDownloadAsync(string key)
    {
        try
        {
            using var r = await Http.PostAsJsonAsync("/api/v1/license/download", new { key });
            var j = JsonNode.Parse(await r.Content.ReadAsStringAsync());
            if (r.IsSuccessStatusCode && j?["url"]?.GetValue<string>() is { Length: > 0 } u) return (Edition.SiteUrl + u, "");
            return ("", j?["message"]?.GetValue<string>() ?? "The download isn't available right now.");
        }
        catch (Exception) { return ("", "Couldn't reach the SFHud website."); }
    }

    /// <summary>Salted SHA-256 of Windows' MachineGuid. The raw id never leaves the PC.</summary>
    public static string MachineHash()
    {
        string guid = "";
        try
        {
            using var k = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            guid = k?.GetValue("MachineGuid") as string ?? "";
        }
        catch (Exception) { }
        if (guid.Length == 0) guid = Environment.MachineName + "|" + Environment.UserName;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("sfhud-machine-v1|" + guid))).ToLowerInvariant();
    }

    private static string Clip(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : (s ?? "").Trim();
}
