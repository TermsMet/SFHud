using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace SFHud.Services;

/// <summary>
/// Owns userdata.json: load (with corruption recovery), debounced autosave, rolling backups,
/// migration from SFHud 0.x, and import / export. Every change goes through <see cref="MarkDirty"/>.
/// </summary>
public static class DataStore
{
    public static string DataPath => Path.Combine(SettingsStore.DataDir, "userdata.json");

    private static readonly DispatcherTimer SaveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1200) };
    private static DateTime _lastBackup = DateTime.MinValue;

    public static UserData Data { get; private set; } = new();
    public static bool Dirty { get; private set; }
    public static DateTime LastSaved { get; private set; }
    /// <summary>Set when loading had to recover from a damaged file; shown once on the dashboard.</summary>
    public static string? LoadWarning { get; set; }

    public static UserProfile Profile => Data.Profiles.FirstOrDefault(p => p.Id == Data.ActiveProfileId) ?? Data.Profiles[0];
    public static ServerPreset Preset => Profile.ActivePreset;
    public static AppSettings Settings => Profile.Settings;

    /// <summary>Saved state changed (dirty → saved, or saved → dirty). For the save indicator.</summary>
    public static event Action? SaveStateChanged;
    /// <summary>Active profile or preset changed: everything should re-read.</summary>
    public static event Action? ContextChanged;
    /// <summary>Notes or pads changed.</summary>
    public static event Action? NotesChanged;

    static DataStore()
    {
        SaveTimer.Tick += (_, _) => SaveNow();
        QuietTimer.Tick += (_, _) => { QuietTimer.Stop(); SaveNow(); };
    }

    // ================================================================ load

    public static void Load()
    {
        SettingsStore.EnsureFolders();
        UserData? data = null;
        if (File.Exists(DataPath))
        {
            data = TryRead(DataPath, out string? err);
            if (data is null)
            {
                string bad = Path.Combine(SettingsStore.DataDir, $"userdata.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                try { File.Copy(DataPath, bad, overwrite: true); } catch (Exception) { }
                Log.Error("userdata.json unreadable: " + err);
                foreach (var b in Backups())
                {
                    data = TryRead(b.FullName, out _);
                    if (data is not null)
                    {
                        LoadWarning = $"Your data file was damaged, so SFHud restored the backup from {b.LastWriteTime:MMM d, HH:mm}. The damaged copy was kept as {Path.GetFileName(bad)}.";
                        break;
                    }
                }
                LoadWarning ??= $"Your data file was damaged and no backup could be read. SFHud started fresh; the damaged copy was kept as {Path.GetFileName(bad)}.";
            }
        }
        else if (File.Exists(SettingsStore.LegacySettingsPath) || File.Exists(SettingsStore.LegacyNotesPath)
                 || File.Exists(Path.Combine(SettingsStore.LegacyDataDir, "settings.json")))
        {
            data = Migration.FromV0();
            Log.Info("migrated SFHud 0.x settings and notes");
        }

        Data = data ?? CreateFresh();
        Normalize(Data);
        EnsureWelcome(Data);
        Data.AppVersion = AppInfo.Version;
        if (!File.Exists(DataPath)) SaveNow();
        Backup("startup");
    }

    private static UserData? TryRead(string path, out string? error)
    {
        error = null;
        try
        {
            var d = JsonSerializer.Deserialize<UserData>(File.ReadAllText(path), SettingsStore.Json);
            if (d is null || d.Profiles.Count == 0) { error = "empty"; return null; }
            return d;
        }
        catch (Exception ex) { error = ex.Message; return null; }
    }

    public static UserData CreateFresh()
    {
        var d = new UserData();
        var p = CreateProfile("Main RP");
        d.Profiles.Add(p);
        d.ActiveProfileId = p.Id;
        return d;
    }

    public static UserProfile CreateProfile(string name)
    {
        var p = new UserProfile { Name = name };
        p.Presets = PresetCatalog.CreateAll();
        p.ActivePresetId = p.Presets[0].Id;
        p.TimerPresets = PresetCatalog.DefaultTimers();
        p.CrosshairPresets = PresetCatalog.DefaultCrosshairs();
        return p;
    }

    /// <summary>
    /// Adds the "Welcome to SFHud" guide (pinned, top of each preset's first pad) once per profile.
    /// Fresh presets already have it; this covers data from earlier versions. Deleting it later sticks.
    /// </summary>
    private static void EnsureWelcome(UserData d)
    {
        foreach (var prof in d.Profiles)
        {
            if (prof.Settings.WelcomeNoteAdded) continue;
            foreach (var pre in prof.Presets)
            {
                var first = pre.Pads.FirstOrDefault(x => x.Pinned) ?? pre.Pads.FirstOrDefault();
                if (first is null) continue;
                // Already there (fresh preset) → just make sure it's the pinned first note.
                if (first.Notes.FirstOrDefault() is { } top && PresetCatalog.IsWelcome(top) && top.Text == PresetCatalog.WelcomeText) { top.Pinned = true; continue; }
                PresetCatalog.AddWelcome(pre);
            }
            prof.Settings.WelcomeNoteAdded = true;
            Dirty = true;
            SaveTimer.Start();
        }
    }

    /// <summary>Repairs anything missing or out of range (older files, hand edits, imports).</summary>
    public static void Normalize(UserData d)
    {
        if (d.Profiles.Count == 0) d.Profiles.Add(CreateProfile("Main RP"));
        if (d.Profiles.All(p => p.Id != d.ActiveProfileId)) d.ActiveProfileId = d.Profiles[0].Id;
        foreach (var prof in d.Profiles)
        {
            prof.Settings ??= new AppSettings();
            prof.Keybinds ??= new();
            prof.TimerPresets ??= new();
            prof.CrosshairPresets ??= new();
            prof.CustomThemes ??= new();
            prof.RecentPads ??= new();
            if (prof.Presets is null || prof.Presets.Count == 0) prof.Presets = PresetCatalog.CreateAll();
            if (prof.Presets.All(p => p.Id != prof.ActivePresetId)) prof.ActivePresetId = prof.Presets[0].Id;
            foreach (var pre in prof.Presets) NormalizePreset(pre);
        }
    }

    public static void NormalizePreset(ServerPreset pre)
    {
        pre.Pages ??= new();
        if (pre.Pages.Count == 0) pre.Pages.Add(new OverlayPage { Name = "Main HUD" });
        foreach (var page in pre.Pages) page.Elements ??= new();
        if (pre.Pages.All(p => p.Id != pre.DefaultPageId)) pre.DefaultPageId = pre.Pages[0].Id;
        if (pre.Pages.All(p => p.Id != pre.ActivePageId)) pre.ActivePageId = pre.DefaultPageId;
        pre.Pads ??= new();
        if (pre.Pads.Count == 0) pre.Pads.Add(new NotePad { Title = "Notes" });
        foreach (var pad in pre.Pads) { pad.Notes ??= new(); pad.Trash ??= new(); }
        pre.Categories ??= new();
        foreach (var c in pre.Pads.Select(p => p.Category).Where(c => !string.IsNullOrWhiteSpace(c)))
            if (!pre.Categories.Contains(c)) pre.Categories.Add(c);
        if (pre.Categories.Count == 0) pre.Categories.Add("General");
        pre.Layouts ??= new();
        // 1.1: the status strip no longer hugs the very corner.
        foreach (var e in pre.Pages.SelectMany(pg => pg.Elements).Where(e => e.Kind == ElementKind.Strip && e.X == 0 && e.Y == 0)) { e.X = 0.012; e.Y = 0.018; }
        pre.Defcon ??= DefconConfig.Default();
        pre.Defcon.Levels ??= new();
        pre.Defcon.Detection ??= new();
    }

    // ================================================================ save

    private static DateTime _dirtySince;
    private static readonly DispatcherTimer QuietTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(6) };

    /// <summary>
    /// Something changed: save shortly. Changes in a burst (typing, dragging a slider) are written once,
    /// about a second after they stop, and at least every 8 s while they keep coming.
    /// </summary>
    public static void MarkDirty()
    {
        if (!Dirty) { Dirty = true; _dirtySince = DateTime.Now; SaveStateChanged?.Invoke(); }
        if ((DateTime.Now - _dirtySince).TotalSeconds > 8) { SaveNow(); return; }
        SaveTimer.Stop();
        SaveTimer.Start();
    }

    /// <summary>
    /// Window position / size changed. Saved quietly a few seconds after you stop moving it (and on
    /// close), without flashing the SAVING indicator for every pixel of a drag.
    /// </summary>
    public static void MarkDirtyQuiet()
    {
        if (Dirty) return; // a normal save is already on its way and will include it
        QuietTimer.Stop();
        QuietTimer.Start();
    }

    public static bool SaveNow()
    {
        SaveTimer.Stop();
        QuietTimer.Stop();
        Data.SavedAt = DateTime.Now;
        Data.AppVersion = AppInfo.Version;
        bool ok = SettingsStore.WriteJson(DataPath, Data);
        if (ok)
        {
            Dirty = false;
            LastSaved = DateTime.Now;
            if (DateTime.Now - _lastBackup > TimeSpan.FromMinutes(30)) Backup("auto");
        }
        SaveStateChanged?.Invoke();
        return ok;
    }

    public static void NotifyNotes()
    {
        NotesChanged?.Invoke();
        MarkDirty();
    }

    public static void NotifyContext()
    {
        Normalize(Data);
        ContextChanged?.Invoke();
        MarkDirty();
    }

    // ================================================================ backups

    public static IEnumerable<FileInfo> Backups()
    {
        try { return new DirectoryInfo(SettingsStore.BackupDir).GetFiles("userdata-*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList(); }
        catch (Exception) { return Enumerable.Empty<FileInfo>(); }
    }

    public static string? Backup(string reason)
    {
        try
        {
            if (!File.Exists(DataPath)) return null;
            Directory.CreateDirectory(SettingsStore.BackupDir);
            string path = Path.Combine(SettingsStore.BackupDir, $"userdata-{DateTime.Now:yyyyMMdd-HHmmss}-{reason}.json");
            File.Copy(DataPath, path, overwrite: true);
            _lastBackup = DateTime.Now;
            int keep = Math.Clamp(Data.Profiles.Count > 0 ? Settings.BackupKeep : 20, 3, 200);
            foreach (var old in Backups().Skip(keep)) old.Delete();
            return path;
        }
        catch (Exception ex) { Log.Error("backup", ex); return null; }
    }

    /// <summary>Replace all data with a backup (the current state is backed up first).</summary>
    public static bool RestoreBackup(string path)
    {
        var d = TryRead(path, out string? err);
        if (d is null) { Log.Warn("restore failed: " + err); return false; }
        SaveNow();
        Backup("before-restore");
        Data = d;
        Normalize(Data);
        SaveNow();
        ContextChanged?.Invoke();
        NotesChanged?.Invoke();
        return true;
    }

    // ================================================================ import / export

    public static T DeepClone<T>(T value) => JsonExt.Clone(value);

    public static void ExportAll(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Data, SettingsStore.Json));

    public static bool ImportAll(string path) => RestoreBackup(path);

    public static void ExportJson<T>(string path, string kind, T value)
    {
        var node = new JsonObject
        {
            ["sfhud"] = kind,
            ["version"] = AppInfo.Version,
            ["data"] = JsonSerializer.SerializeToNode(value, SettingsStore.Json),
        };
        File.WriteAllText(path, node.ToJsonString(SettingsStore.Json));
    }

    public static T? ImportJson<T>(string path, string kind) where T : class
    {
        var node = JsonNode.Parse(File.ReadAllText(path));
        if ((node?["sfhud"] ?? node?["shud"])?.ToString() != kind) throw new InvalidDataException($"That file isn't an SFHud {kind} export.");
        return node!["data"].Deserialize<T>(SettingsStore.Json);
    }

    /// <summary>Gives every id inside a preset a fresh value, keeping internal links (pad ↔ element ↔ level) intact.</summary>
    public static ServerPreset Reidentify(ServerPreset src)
    {
        var p = DeepClone(src);
        var map = new Dictionary<string, string>();
        string Re(string old) { if (string.IsNullOrEmpty(old)) return old; if (!map.TryGetValue(old, out var n)) map[old] = n = Ids.New(); return n; }

        p.Id = Ids.New();
        foreach (var pad in p.Pads) pad.Id = Re(pad.Id);
        foreach (var page in p.Pages) page.Id = Re(page.Id);
        foreach (var l in p.Defcon.Levels) l.Id = Re(l.Id);
        foreach (var page in p.Pages)
            foreach (var e in page.Elements)
            {
                e.Id = Ids.New();
                if (e.PadId.Length > 0) e.PadId = map.GetValueOrDefault(e.PadId, e.PadId);
                e.DefconFilter = e.DefconFilter.Select(f => map.GetValueOrDefault(f, f)).ToList();
            }
        foreach (var l in p.Defcon.Levels)
        {
            l.AutoPageId = map.GetValueOrDefault(l.AutoPageId, l.AutoPageId);
            l.PadId = map.GetValueOrDefault(l.PadId, l.PadId);
        }
        foreach (var pad in p.Pads) pad.DefconLevelId = map.GetValueOrDefault(pad.DefconLevelId, pad.DefconLevelId);
        p.DefaultPageId = map.GetValueOrDefault(p.DefaultPageId, p.DefaultPageId);
        p.ActivePageId = map.GetValueOrDefault(p.ActivePageId, p.ActivePageId);
        p.ActivePadId = map.GetValueOrDefault(p.ActivePadId, p.ActivePadId);
        p.Defcon.ActiveLevelId = map.GetValueOrDefault(p.Defcon.ActiveLevelId, p.Defcon.ActiveLevelId);
        NormalizePreset(p);
        return p;
    }

    // ================================================================ profiles + presets

    public static void SwitchProfile(UserProfile p)
    {
        if (p.Id == Data.ActiveProfileId) return;
        Data.ActiveProfileId = p.Id;
        NotifyContext();
        NotesChanged?.Invoke();
    }

    public static void SwitchPreset(ServerPreset p)
    {
        if (p.Id == Profile.ActivePresetId) return;
        Profile.ActivePresetId = p.Id;
        NotifyContext();
        NotesChanged?.Invoke();
    }

    public static UserProfile DuplicateProfile(UserProfile src)
    {
        var p = DeepClone(src);
        p.Id = Ids.New();
        p.Name = UniqueName(src.Name + " copy", Data.Profiles.Select(x => x.Name));
        p.Created = DateTime.Now;
        p.Presets = p.Presets.Select(Reidentify).ToList();
        p.ActivePresetId = p.Presets[Math.Max(0, src.Presets.FindIndex(x => x.Id == src.ActivePresetId))].Id;
        Data.Profiles.Add(p);
        MarkDirty();
        return p;
    }

    public static ServerPreset DuplicatePreset(ServerPreset src)
    {
        var p = Reidentify(src);
        p.Name = UniqueName(src.Name + " copy", Profile.Presets.Select(x => x.Name));
        p.Style = PresetStyle.Custom;
        Profile.Presets.Insert(Profile.Presets.IndexOf(src) + 1, p);
        MarkDirty();
        return p;
    }

    public static string UniqueName(string wanted, IEnumerable<string> taken)
    {
        var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!set.Contains(wanted)) return wanted;
        for (int i = 2; ; i++) if (!set.Contains($"{wanted} {i}")) return $"{wanted} {i}";
    }
}

public static class AppInfo
{
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public const string DonateUrl = "https://ko-fi.com/theslorb";
}

/// <summary>SFHud 0.x kept settings.json + notes.json. Carry everything useful into a "Main RP" profile.</summary>
internal static class Migration
{
    public static UserData FromV0()
    {
        string dir = SettingsStore.DataDir;
        string settingsPath = File.Exists(SettingsStore.LegacySettingsPath)
            ? SettingsStore.LegacySettingsPath
            : Path.Combine(SettingsStore.LegacyDataDir, "settings.json");
        var data = DataStore.CreateFresh();
        var prof = data.Profiles[0];

        JsonObject? old = null;
        try { if (File.Exists(settingsPath)) old = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject; }
        catch (Exception ex) { Log.Warn("legacy settings unreadable: " + ex.Message); }

        if (old is not null)
        {
            try
            {
                // Same property names carry straight over; unknown ones are ignored.
                var s = old.Deserialize<AppSettings>(SettingsStore.Json);
                if (s is not null) prof.Settings = s;
            }
            catch (Exception ex) { Log.Warn("legacy settings partial: " + ex.Message); }

            string oldProfile = old["ProfileId"]?.ToString() ?? "cg";
            var style = oldProfile switch { "zs" => PresetStyle.Zombie, "darkrp" or "sandbox" => PresetStyle.General, _ => PresetStyle.Swrp };
            prof.ActivePresetId = prof.Presets.First(p => p.Style == style).Id;
            if (int.TryParse(old["QuickTimerMinutes"]?.ToString(), out int qm)) prof.Settings.QuickTimerMinutes = qm;
        }

        // Old quick notes go into the active preset's first pad, keeping their dates and flags.
        try
        {
            if (File.Exists(SettingsStore.LegacyNotesPath) && JsonNode.Parse(File.ReadAllText(SettingsStore.LegacyNotesPath)) is JsonArray arr)
            {
                var pad = prof.ActivePreset.Pads[0];
                foreach (var n in arr.OfType<JsonObject>())
                {
                    string text = n["Text"]?.ToString() ?? "";
                    if (text.Length == 0) continue;
                    bool done = n["TaskDone"]?.GetValue<bool>() == true;
                    if (done) text = MarkTaskDone(text);
                    DateTime.TryParse(n["Created"]?.ToString(), out var created);
                    pad.Notes.Add(new PadNote
                    {
                        Text = text,
                        Created = created == default ? DateTime.Now : created,
                        Modified = created == default ? DateTime.Now : created,
                        Pinned = n["Pinned"]?.GetValue<bool>() == true,
                        HiddenOnOverlay = n["HiddenOnOverlay"]?.GetValue<bool>() == true,
                    });
                }
                pad.Notes = pad.Notes.OrderByDescending(x => x.Created).ToList();
            }
        }
        catch (Exception ex) { Log.Warn("legacy notes unreadable: " + ex.Message); }

        return data;
    }

    private static string MarkTaskDone(string text)
    {
        foreach (var p in new[] { "- [ ]", "[ ]" })
            if (text.TrimStart().StartsWith(p, StringComparison.OrdinalIgnoreCase)) return "- [x]" + text.TrimStart()[p.Length..];
        return text;
    }
}
