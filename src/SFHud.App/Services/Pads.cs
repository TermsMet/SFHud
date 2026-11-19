using System;
using System.Collections.Generic;
using System.Linq;

namespace SFHud.Services;

public sealed record NoteHit(NotePad Pad, PadNote Note);

/// <summary>Note pad operations. Every change is saved through DataStore (debounced autosave).</summary>
public static class Pads
{
    public const int MaxPads = 60;
    public const int MaxTrash = 50;

    public static ServerPreset P => DataStore.Preset;

    /// <summary>Pads available right now: unscoped ones plus those tied to the active DEFCON level.</summary>
    public static IEnumerable<NotePad> Visible()
    {
        string level = DefconService.Enabled ? P.Defcon.ActiveLevelId : "";
        // Per-level pads are SFHud Pro; in Free they simply show at every level (nothing is hidden or lost).
        bool scoped = Edition.Has(ProFeature.LevelLayouts);
        return Ordered().Where(p => !scoped || p.DefconLevelId.Length == 0 || p.DefconLevelId == level);
    }

    /// <summary>Pinned tabs first, then the saved order.</summary>
    public static IEnumerable<NotePad> Ordered() => P.Pads.Where(p => p.Pinned).Concat(P.Pads.Where(p => !p.Pinned));

    public static NotePad Current
    {
        get
        {
            var vis = Visible().ToList();
            return vis.FirstOrDefault(p => p.Id == P.ActivePadId) ?? vis.FirstOrDefault() ?? P.Pads[0];
        }
    }

    public static void Select(NotePad pad)
    {
        P.ActivePadId = pad.Id;
        pad.LastUsed = DateTime.Now;
        var recent = DataStore.Profile.RecentPads;
        recent.Remove(pad.Id);
        recent.Insert(0, pad.Id);
        while (recent.Count > 8) recent.RemoveAt(recent.Count - 1);
        DataStore.NotifyNotes();
    }

    public static IEnumerable<NotePad> Recent() =>
        DataStore.Profile.RecentPads.Select(id => P.Pad(id)).Where(p => p is not null).Cast<NotePad>();

    public static void Step(int dir)
    {
        var vis = Visible().ToList();
        if (vis.Count == 0) return;
        int i = Math.Max(0, vis.IndexOf(Current));
        Select(vis[(i + dir + vis.Count) % vis.Count]);
    }

    public static NotePad Create(string title, string category = "General")
    {
        if (P.Pads.Count >= MaxPads) throw new InvalidOperationException($"You can have up to {MaxPads} note pads per preset.");
        var pad = new NotePad { Title = DataStore.UniqueName(title, P.Pads.Select(x => x.Title)), Category = category };
        P.Pads.Add(pad);
        if (!P.Categories.Contains(category)) P.Categories.Add(category);
        DataStore.NotifyNotes();
        return pad;
    }

    public static NotePad Duplicate(NotePad src)
    {
        var pad = DataStore.DeepClone(src);
        pad.Id = Ids.New();
        pad.Title = DataStore.UniqueName(src.Title + " copy", P.Pads.Select(x => x.Title));
        pad.Pinned = false;
        foreach (var n in pad.Notes) n.Id = Ids.New();
        pad.Trash.Clear();
        P.Pads.Insert(P.Pads.IndexOf(src) + 1, pad);
        DataStore.NotifyNotes();
        return pad;
    }

    public static void Delete(NotePad pad)
    {
        if (P.Pads.Count <= 1) throw new InvalidOperationException("A preset needs at least one note pad.");
        DataStore.Backup("pad-deleted");
        P.Pads.Remove(pad);
        foreach (var page in P.Pages) page.Elements.RemoveAll(e => e.Kind == ElementKind.NotePad && e.PadId == pad.Id);
        if (P.ActivePadId == pad.Id) P.ActivePadId = P.Pads[0].Id;
        DataStore.Profile.Keybinds.RemoveAll(k => k.Action.EndsWith("." + pad.Id));
        DataStore.NotifyNotes();
        DataStore.NotifyContext();
    }

    /// <summary>Drag-and-drop reorder within the full list.</summary>
    public static void Move(NotePad pad, NotePad before)
    {
        if (pad == before) return;
        P.Pads.Remove(pad);
        int i = P.Pads.IndexOf(before);
        P.Pads.Insert(i < 0 ? P.Pads.Count : i, pad);
        DataStore.NotifyNotes();
    }

    public static void Shift(NotePad pad, int dir)
    {
        int i = P.Pads.IndexOf(pad), j = Math.Clamp(i + dir, 0, P.Pads.Count - 1);
        if (i == j) return;
        P.Pads.RemoveAt(i);
        P.Pads.Insert(j, pad);
        DataStore.NotifyNotes();
    }

    // ================================================================ notes

    public static PadNote Add(NotePad pad, string text)
    {
        var n = new PadNote { Text = text.TrimEnd() };
        pad.Notes.Insert(0, n);
        pad.LastUsed = DateTime.Now;
        DataStore.NotifyNotes();
        return n;
    }

    public static void Edit(PadNote note, string text)
    {
        if (note.Text == text) return;
        note.Text = text;
        note.Modified = DateTime.Now;
        DataStore.NotifyNotes();
    }

    public static void Remove(NotePad pad, PadNote note)
    {
        pad.Notes.Remove(note);
        pad.Trash.Insert(0, note);
        while (pad.Trash.Count > MaxTrash) pad.Trash.RemoveAt(pad.Trash.Count - 1);
        DataStore.NotifyNotes();
    }

    public static int Clear(NotePad pad)
    {
        int n = pad.Notes.Count;
        if (n == 0) return 0;
        DataStore.Backup("pad-cleared");
        foreach (var note in pad.Notes) pad.Trash.Insert(0, note);
        while (pad.Trash.Count > MaxTrash) pad.Trash.RemoveAt(pad.Trash.Count - 1);
        pad.Notes.Clear();
        DataStore.NotifyNotes();
        return n;
    }

    /// <summary>Brings back the most recently deleted note (or the whole last clear).</summary>
    public static int Undo(NotePad pad, int count = 1)
    {
        int n = 0;
        while (n < count && pad.Trash.Count > 0)
        {
            var note = pad.Trash[0];
            pad.Trash.RemoveAt(0);
            pad.Notes.Insert(0, note);
            n++;
        }
        pad.Notes = pad.Notes.OrderByDescending(x => x.Pinned).ThenByDescending(x => x.Created).ToList();
        if (n > 0) DataStore.NotifyNotes();
        return n;
    }

    public static PadNote DuplicateNote(NotePad pad, PadNote src)
    {
        var n = DataStore.DeepClone(src);
        n.Id = Ids.New();
        n.Created = n.Modified = DateTime.Now;
        pad.Notes.Insert(Math.Max(0, pad.Notes.IndexOf(src)), n);
        DataStore.NotifyNotes();
        return n;
    }

    public static void MoveNote(NotePad from, PadNote note, NotePad to)
    {
        if (from == to) return;
        from.Notes.Remove(note);
        to.Notes.Insert(0, note);
        DataStore.NotifyNotes();
    }

    public static void ToggleTask(PadNote note, int line)
    {
        note.Text = NoteMarkup.ToggleTask(note.Text, line);
        note.Modified = DateTime.Now;
        DataStore.NotifyNotes();
    }

    public static void ResizeImage(PadNote note, int line, double width)
    {
        note.Text = NoteImages.Resize(note.Text, line, width);
        note.Modified = DateTime.Now;
        DataStore.NotifyNotes();
    }

    public static void RemoveImage(PadNote note, int line)
    {
        note.Text = NoteImages.RemoveLine(note.Text, line);
        note.Modified = DateTime.Now;
        DataStore.NotifyNotes();
    }

    /// <summary>Finds a note (and its pad) anywhere in the current preset.</summary>
    public static (NotePad Pad, PadNote Note)? FindNote(string noteId)
    {
        foreach (var p in P.Pads)
            if (p.Notes.FirstOrDefault(n => n.Id == noteId) is { } n) return (p, n);
        return null;
    }

    public static void TogglePin(PadNote note)
    {
        note.Pinned = !note.Pinned;
        DataStore.NotifyNotes();
    }

    public static void ToggleHidden(PadNote note)
    {
        note.HiddenOnOverlay = !note.HiddenOnOverlay;
        DataStore.NotifyNotes();
    }

    /// <summary>What a pad element on the overlay shows: pinned notes first, then the newest, skipping hidden ones.</summary>
    public static List<PadNote> OverlayNotes(NotePad pad, int count)
    {
        var vis = pad.Notes.Where(n => !n.HiddenOnOverlay).ToList();
        return vis.Where(n => n.Pinned).Concat(vis.Where(n => !n.Pinned)).Take(Math.Max(1, count)).ToList();
    }

    public static IEnumerable<NoteHit> Search(string query, bool allPresets = false)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) yield break;
        var presets = allPresets ? DataStore.Profile.Presets : new List<ServerPreset> { P };
        foreach (var pre in presets)
            foreach (var pad in pre.Pads)
                foreach (var n in pad.Notes)
                    if (terms.All(t => n.Text.Contains(t, StringComparison.OrdinalIgnoreCase) || n.Label.Contains(t, StringComparison.OrdinalIgnoreCase) || pad.Title.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        yield return new NoteHit(pad, n);
    }

    public static string PadAsText(NotePad pad) =>
        $"# {pad.Title}\n\n" + string.Join("\n\n", pad.Notes.Select(n => n.Text));

    /// <summary>Imports a .txt / .md document as notes (split at headings or blank-line paragraphs).</summary>
    public static NotePad ImportDocument(string title, string text)
    {
        var pad = Create(title, "Documents");
        var chunks = new List<string>();
        var cur = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("# ") || raw.StartsWith("## "))
            {
                if (cur.Any(l => l.Trim().Length > 0)) chunks.Add(string.Join("\n", cur).Trim());
                cur.Clear();
            }
            cur.Add(raw);
        }
        if (cur.Any(l => l.Trim().Length > 0)) chunks.Add(string.Join("\n", cur).Trim());
        if (chunks.Count <= 1 && text.Length > 1500)
            chunks = text.Replace("\r", "").Split("\n\n").Where(c => c.Trim().Length > 0).Select(c => c.Trim()).ToList();
        foreach (var c in chunks.Take(400)) pad.Notes.Add(new PadNote { Text = c }); // document order, top to bottom
        DataStore.NotifyNotes();
        return pad;
    }
}
