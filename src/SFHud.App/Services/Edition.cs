using System;
using System.Collections.Generic;

namespace SFHud.Services;

/// <summary>Features that used to be a paid add-on. All of them are free now; the list stays so
/// feature checks and descriptions keep working in one place.</summary>
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
    ChatAlerts,
}

/// <summary>
/// SFHud is one free, open-source edition with every feature. There is no license, account or
/// hardware-id check of any kind. Support development with a donation (Ko-fi) if you like it.
/// </summary>
public static class Edition
{
    /// <summary>Ko-fi page for donations.</summary>
    public const string DonateUrl = AppInfo.DonateUrl;

    /// <summary>Public source and releases (also where updates come from).</summary>
    public const string RepoUrl = "https://github.com/TermsMet/SFHud";

    /// <summary>Always true: everything is included.</summary>
    public static bool Unlocked => true;

#pragma warning disable CS0067 // kept so older call sites compile; nothing changes at runtime anymore
    public static event Action? Changed;
#pragma warning restore CS0067

    public static bool Available(ProFeature f) => true;
    public static bool Has(ProFeature f) => true;

    public static string Name => "SFHud";

    public static bool CanAddPage(int existing) => true;
    public static bool CanAddProfile(int existing) => true;
    public static bool PageUsable(int index) => true;
    public static bool ProfileUsable(int index) => true;

    public static string Describe(ProFeature f) => f switch
    {
        ProFeature.LocalAi => "Local AI assistant",
        ProFeature.DefconAutoDetect => "DEFCON auto-detection",
        ProFeature.LevelLayouts => "Layout and notes per DEFCON level",
        ProFeature.Video => "Video on the overlay",
        ProFeature.UnlimitedPages => "Overlay pages",
        ProFeature.UnlimitedProfiles => "Profiles",
        ProFeature.CustomThemes => "Custom themes",
        ProFeature.Companion => "Companion window",
        ProFeature.ChatAlerts => "Chat alerts",
        _ => f.ToString(),
    };

    /// <summary>No upgrade page anymore; kept null so old feature chips do nothing.</summary>
    public static Action<ProFeature?>? ShowUpgrade { get; set; }
}
