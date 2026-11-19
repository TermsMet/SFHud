using System.Linq;

namespace SFHud.Services;

/// <summary>
/// One overlay page per DEFCON level: which page a level shows, and making a new one from an existing page.
/// The level's <see cref="DefconLevel.AutoPageId"/> is the link; this just makes it easy to set up.
/// </summary>
public static class DefconLayouts
{
    private static ServerPreset P => DataStore.Preset;

    public static OverlayPage? PageFor(DefconLevel level) => P.Pages.FirstOrDefault(p => p.Id == level.AutoPageId);

    public static string PageName(DefconLevel level) => PageFor(level)?.Name ?? "Stay on current page";

    public static void Assign(DefconLevel level, string pageId)
    {
        level.AutoPageId = P.Pages.Any(p => p.Id == pageId) ? pageId : "";
        DataStore.MarkDirty();
    }

    /// <summary>Copies <paramref name="from"/> (the current page by default) into a new page named after the level and links it.</summary>
    public static OverlayPage? CreateFor(DefconLevel level, OverlayPage? from = null)
    {
        if (P.Pages.Count >= 30 || !Edition.Has(ProFeature.LevelLayouts) || !Edition.CanAddPage(P.Pages.Count)) return null;
        from ??= P.ActivePage;
        var page = DataStore.DeepClone(from);
        page.Id = Ids.New();
        page.Name = DataStore.UniqueName(level.Label + " layout", P.Pages.Select(p => p.Name));
        foreach (var e in page.Elements)
        {
            e.Id = Ids.New();
            // A page made for one level shouldn't hide its own elements behind other levels' filters.
            e.DefconFilter.Clear();
        }
        P.Pages.Add(page);
        level.AutoPageId = page.Id;
        DataStore.NotifyContext();
        return page;
    }

    /// <summary>The page to show after a level change, or null to stay where we are.</summary>
    public static string? Target(DefconLevel? old, DefconLevel? now)
    {
        if (!Edition.Has(ProFeature.LevelLayouts)) return null;
        if (now is not null && now.AutoPageId.Length > 0 && P.Pages.Any(p => p.Id == now.AutoPageId)) return now.AutoPageId;
        // Leaving a level that had its own layout: go back to the default page instead of staying on e.g. "LOCKDOWN layout".
        if (P.Defcon.ReturnToDefaultPage && old is not null && old.AutoPageId.Length > 0 && P.ActivePageId == old.AutoPageId)
            return P.DefaultPageId.Length > 0 ? P.DefaultPageId : P.Pages[0].Id;
        return null;
    }
}
