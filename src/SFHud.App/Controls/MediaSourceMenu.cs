using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SFHud.Services;

namespace SFHud.Controls;

/// <summary>
/// The "listen to" picker for Now Playing, shared by Home, Settings › Music and the overlay player.
/// ✓ = selected, ● = something is playing there right now.
/// </summary>
public static class MediaSourceMenu
{
    public static void Open(UIElement anchor, Action? changed = null, Action? openSettings = null)
    {
        var svc = MediaService.Instance;
        var s = DataStore.Settings;
        var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        m.Items.Add(new MenuItem { Header = "LISTEN TO", IsEnabled = false });
        foreach (var mode in MediaService.Choices)
        {
            bool sel = s.MediaSessionId.Length == 0 && s.MediaMode == mode;
            var mi = new MenuItem { Header = $"{(sel ? "✓  " : "     ")}{MediaService.Describe(mode)}{(mode != MediaMode.Auto && svc.IsModePlaying(mode) ? "   ●" : "")}" };
            var mm = mode;
            mi.Click += (_, _) => { svc.SetMode(mm); changed?.Invoke(); };
            m.Items.Add(mi);
        }

        var players = svc.SourcesQuick();
        if (players.Count > 0)
        {
            m.Items.Add(new Separator());
            m.Items.Add(new MenuItem { Header = "PIN ONE PLAYER", IsEnabled = false });
            foreach (var p in players)
            {
                bool pinned = s.MediaSessionId == p.Id;
                var mi = new MenuItem { Header = $"{(pinned ? "✓  " : "     ")}{p.App}{(p.Playing ? "   ● playing" : "   paused")}", ToolTip = "Follow only this player (click again to unpin)" };
                var id = p.Id;
                mi.Click += (_, _) => { svc.PinSession(id); changed?.Invoke(); };
                m.Items.Add(mi);
            }
        }

        if (openSettings is not null)
        {
            m.Items.Add(new Separator());
            var more = new MenuItem { Header = "Music settings…" };
            more.Click += (_, _) => openSettings();
            m.Items.Add(more);
        }
        m.IsOpen = true;
    }
}
