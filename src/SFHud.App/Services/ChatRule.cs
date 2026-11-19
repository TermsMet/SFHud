using System.Collections.Generic;

namespace SFHud.Services;

/// <summary>Something to look out for in server chat: who says it (roles / names) and what they say.</summary>
public sealed class ChatRule
{
    public string Id { get; set; } = Ids.New();
    public string Name { get; set; } = "Rule";
    public bool Enabled { get; set; } = true;
    /// <summary>Comma separated chat roles / ranks / channels to listen to ("admin, event team"). Empty = anyone.</summary>
    public string Roles { get; set; } = "";
    /// <summary>Comma separated player names to listen to. Empty = anyone.</summary>
    public string Names { get; set; } = "";
    /// <summary>Comma separated words or phrases; any one matches. Empty = every message from those roles / names.</summary>
    public string Phrases { get; set; } = "";
    /// <summary>On-screen title. {n} becomes the minutes found in the message ("in 10 minutes").</summary>
    public string Title { get; set; } = "";
    /// <summary>Second line. {message} {name} {role} {n} are filled in.</summary>
    public string Text { get; set; } = "{message}";
    /// <summary>When the message gives a time ("in 10 min"), start a timer and say again when it's due.</summary>
    public bool Countdown { get; set; } = true;
    public bool Urgent { get; set; }
    public string Sound { get; set; } = "";

    public static List<ChatRule> Defaults() => new()
    {
        new()
        {
            Name = "Events", Roles = "admin, superadmin, owner, staff, moderator, mod, event, event team, gm, game master",
            Phrases = "event, events, starting, start in, begins, deploying, deployment, briefing",
            Title = "EVENT", Text = "{message}", Countdown = true, Urgent = true,
        },
        new()
        {
            Name = "Staff announcements", Roles = "admin, superadmin, owner, staff, moderator, mod, announcement, broadcast, server",
            Phrases = "", Title = "STAFF · {name}", Text = "{message}", Countdown = false,
        },
    };
}
