using System;
using System.Collections.Generic;

namespace SFHud.Services;

/// <summary>Everything SFHud Pro unlocks. The Upgrade page's comparison table is built from this list.</summary>
public enum ProFeature
{
    LocalAi,
    DefconAutoDetect,
    LevelLayouts,
    Video,
    UnlimitedPages,
    UnlimitedProfiles,
    CustomThemes,
    Companion,
    MiniPlayer,
    ChatAlerts,
}

/// <summary>
/// Free vs Pro. The free edition is complete on its own and open source; Pro is a one-time purchase
/// (a separate download, activated with a license key) that adds the features in <see cref="ProFeature"/>.
/// Nothing a free user made is ever deleted: over-limit pages and profiles are kept, just locked.
/// </summary>
public static class Edition
{
    public const bool IsProBuild = false;

    /// <summary>Built-in presets ship with up to 4 pages, so free users always have room for one of their own.</summary>
    public const int FreePages = 5;
    public const int FreeProfiles = 1;

    /// <summary>Prices shown in the app (the store page is the source of truth).</summary>
    public const string PriceLifetime = "$30";
    public const string PriceMonthly = "$5";
    /// <summary>Short form for buttons: "$30 once or $5/month".</summary>
    public const string Price = PriceLifetime + " once or " + PriceMonthly + "/month";
    /// <summary>The SFHud website (store, Pro add-on download, trial offers). Change when you move to your own domain.</summary>
    public const string SiteUrl = "https://sfhud.site";
    public const string BuyUrl = SiteUrl + "/store";
    /// <summary>Where Pro is explained / downloaded (the receipt also has the download).</summary>
    public const string ProInfoUrl = SiteUrl + "/download";

    private static bool _unlocked;

    /// <summary>True in a Pro build with an active (or within-grace) license. Set by the license service.</summary>
    public static bool Unlocked
    {
        get => IsProBuild && _unlocked;
        set { if (_unlocked == value) return; _unlocked = value; Changed?.Invoke(); }
    }

    public static event Action? Changed;

    /// <summary>Features that are switched off in every edition for now (kept in the code for later).</summary>
    public static bool Available(ProFeature f) => true;

    public static bool Has(ProFeature f) => Available(f) && Unlocked;

    public static string Name => Unlocked ? "SFHud Pro" : IsProBuild ? "SFHud Pro (not activated)" : "SFHud Free";

    public static bool CanAddPage(int existing) => Has(ProFeature.UnlimitedPages) || existing < FreePages;
    public static bool CanAddProfile(int existing) => Has(ProFeature.UnlimitedProfiles) || existing < FreeProfiles;

    /// <summary>Pages past the free limit stay on disk and visible in lists, but can't be shown or edited.</summary>
    public static bool PageUsable(int index) => Has(ProFeature.UnlimitedPages) || index < FreePages;
    public static bool ProfileUsable(int index) => Has(ProFeature.UnlimitedProfiles) || index < FreeProfiles;

    public static readonly IReadOnlyList<(ProFeature Feature, string Name, string Free, string Pro)> Table = new[]
    {
        (ProFeature.LocalAi, "Local AI assistant", "—", "Offline AI: briefing summaries, note ideas, labels, chat summary, ask anything"),
        (ProFeature.DefconAutoDetect, "DEFCON auto-detection", "Manual, keys, console.log + paste suggestions", "Reads the level off the screen (verified), status URL"),
        (ProFeature.LevelLayouts, "Layout and notes per DEFCON level", "—", "Overlay switches page and shows that level's note pad by itself when the level changes"),
        (ProFeature.Video, "Video on the overlay", "—", "Local files or video links, muted, pauses out of game"),
        (ProFeature.UnlimitedPages, "Overlay pages", $"{FreePages} per preset", "Unlimited"),
        (ProFeature.UnlimitedProfiles, "Profiles", $"{FreeProfiles}", "Unlimited (Main RP, Training, Event…)"),
        (ProFeature.CustomThemes, "Custom themes", "All built-in themes", "Theme editor and background image"),
        (ProFeature.Companion, "Companion window", "—", "Your overlay as a normal window for a second screen"),
        (ProFeature.MiniPlayer, "Pop-up mini player", "Overlay music player and media keys", "Floating player with seek bar; shows itself on the desktop when you leave the game"),
    };

    public static string Describe(ProFeature f)
    {
        foreach (var r in Table) if (r.Feature == f) return r.Name;
        return f.ToString();
    }

    /// <summary>Set by the main window: opens the Upgrade page (from any PRO chip).</summary>
    public static Action<ProFeature?>? ShowUpgrade { get; set; }
}
