using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SFHud.Services;

/// <summary>
/// Where the game draws its chat box. Overlay elements are kept off it so you can always read chat
/// (the place server events are announced). Rectangles are fractions of the game's client area (0..1).
/// Shown as an amber box in customize mode.
/// </summary>
public static class HudZones
{
    public sealed record Zone(string Name, Rect Area);

    /// <summary>Most Source games: chat bottom-left, above health.</summary>
    private static readonly Zone[] Source = { new("CHAT", new Rect(0.00, 0.58, 0.30, 0.27)) };

    private static readonly Dictionary<string, Zone[]> ByGame = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmod"] = Source,
        ["tf2"] = new[] { new Zone("CHAT", new Rect(0.00, 0.55, 0.32, 0.28)) },
        ["cs2"] = new[] { new Zone("CHAT", new Rect(0.00, 0.62, 0.30, 0.24)) },
        ["rust"] = new[] { new Zone("CHAT", new Rect(0.00, 0.60, 0.28, 0.28)) },
        ["sbox"] = Source,
    };

    /// <summary>Zones for a game (none when the user turned the feature off). Unknown games get the usual Source chat area.</summary>
    public static IReadOnlyList<Zone> For(string gameId) =>
        !DataStore.Settings.AvoidGameHud ? Array.Empty<Zone>() : ByGame.TryGetValue(gameId, out var z) ? z : Source;

    /// <summary>
    /// Moves a rectangle the shortest distance so it overlaps no zone and stays within bounds.
    /// Everything is in the same units as <paramref name="bounds"/>; zones are scaled to it.
    /// </summary>
    public static Point Clear(Rect r, Rect bounds, IReadOnlyList<Zone> zones, double gap = 8, Rect? keepWithin = null)
    {
        if (zones.Count == 0) return r.Location;
        var areas = zones.Select(z => new Rect(bounds.X + z.Area.X * bounds.Width, bounds.Y + z.Area.Y * bounds.Height, z.Area.Width * bounds.Width, z.Area.Height * bounds.Height)).ToList();
        bool Free(Rect c) => areas.All(a => !a.IntersectsWith(Inflate(c, gap / 2)));
        if (Free(r)) return r.Location;

        var k = keepWithin ?? bounds;
        Point Fit(double x, double y) => new(Math.Clamp(x, k.Left, Math.Max(k.Left, k.Right - r.Width)), Math.Clamp(y, k.Top, Math.Max(k.Top, k.Bottom - r.Height)));
        // Candidates: just outside each zone edge, and corner combinations of two zones (e.g. above chat AND right of health).
        var xs = new List<double> { r.X };
        var ys = new List<double> { r.Y };
        foreach (var a in areas)
        {
            xs.Add(a.Left - r.Width - gap); xs.Add(a.Right + gap);
            ys.Add(a.Top - r.Height - gap); ys.Add(a.Bottom + gap);
        }
        Point best = r.Location;
        double bestD = double.MaxValue;
        foreach (var x in xs)
            foreach (var y in ys)
            {
                var p = Fit(x, y);
                var c = new Rect(p, r.Size);
                if (!Free(c)) continue;
                double d = (p.X - r.X) * (p.X - r.X) + (p.Y - r.Y) * (p.Y - r.Y);
                if (d < bestD) { bestD = d; best = p; }
            }
        return best; // if nothing fits (huge element), it stays where it was
    }

    private static Rect Inflate(Rect r, double by) { r.Inflate(by, by); return r; }
}
