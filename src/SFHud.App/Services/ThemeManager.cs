using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace SFHud.Services;

/// <summary>
/// Turns a <see cref="ThemeDef"/> plus the user's appearance overrides into the B.* / F.* resources
/// every window binds to with DynamicResource, so switching theme or preset restyles everything live.
/// </summary>
public static class ThemeManager
{
    public static ThemeDef Current { get; private set; } = PresetCatalog.Themes[0];
    public static event Action? Changed;

    public static ThemeDef Resolve()
    {
        var s = DataStore.Settings;
        string id = s.Theme == "preset" ? DataStore.Preset.ThemeId : s.Theme;
        return DataStore.Profile.CustomThemes.FirstOrDefault(t => t.Id == id)
            ?? PresetCatalog.Theme(id)
            ?? PresetCatalog.Themes[0];
    }

    public static void Apply()
    {
        var s = DataStore.Settings;
        var t = DataStore.DeepClone(Resolve());
        if (s.AccentOverride.Length > 0 && NoteMarkup.ParseColor(s.AccentOverride) is not null) t.Accent = s.AccentOverride;
        if (Enum.TryParse<HudCorners>(s.Corners, out var corners)) t.Corners = corners;
        if (s.DisplayFont.Length > 0) t.DisplayFont = s.DisplayFont;
        if (s.BodyFont.Length > 0) t.BodyFont = s.BodyFont;
        Current = t;

        var r = Application.Current.Resources;
        Color C(string hex) => NoteMarkup.ParseColor(hex) ?? Colors.Magenta;
        Color A(Color c, double a) => Color.FromArgb((byte)Math.Round(255 * a), c.R, c.G, c.B);
        SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        Color bg = C(t.Bg), el = C(t.Elevated), ac = C(t.Accent), warn = C(t.Warn), alert = C(t.Alert), ok = C(t.Ok), text = C(t.Text), dim = C(t.Dim);
        double lineA = t.Light ? 0.30 : 0.24, strongA = t.Light ? 0.65 : 0.58;

        r["C.Bg"] = bg;
        r["C.Accent"] = ac;
        r["B.Bg"] = B(bg);
        r["B.BgElevated"] = B(el);
        r["B.Panel"] = B(A(el, t.Light ? 0.92 : 0.78));
        r["B.Card"] = B(A(bg, 0.90));
        r["B.Accent"] = B(ac);
        r["B.Accent.Fill"] = B(A(ac, t.Light ? 0.10 : 0.08));
        r["B.Accent.Fill2"] = B(A(ac, t.Light ? 0.18 : 0.15));
        r["B.Accent.Fill3"] = B(A(ac, 0.25));
        r["B.Accent.Solid"] = B(A(ac, 0.88));
        r["B.Line"] = B(A(ac, lineA));
        r["B.Line.Strong"] = B(A(ac, strongA));
        r["B.Amber"] = B(warn);
        r["B.Amber.Fill"] = B(A(warn, 0.14));
        r["B.Alert"] = B(alert);
        r["B.Alert.Fill"] = B(A(alert, 0.2));
        r["B.Ok"] = B(ok);
        r["B.Text"] = B(text);
        r["B.Dim"] = B(dim);
        r["B.Muted"] = B(A(dim, 0.62));
        r["B.OnAccent"] = B(Luma(ac) > 0.55 ? Color.FromRgb(0x08, 0x0B, 0x10) : Colors.White);

        r["B.Divider"] = Gradient(ac, new[] { 0, 0.58, 0.24, 0 }, new[] { 0, 0.18, 0.7, 1 }, horizontal: true);
        r["B.Divider.Vertical"] = Gradient(ac, new[] { 0, 0.32, 0.12, 0 }, new[] { 0, 0.3, 0.85, 1 }, horizontal: false);
        r["B.Wash.Accent"] = Radial(ac, 0.15, new Point(0.12, 0), 0.75, 0.9);
        r["B.Wash.Amber"] = Radial(warn, 0.08, new Point(0.92, 1), 0.6, 0.7);
        r["B.Scanlines"] = Tiled(ac, 4, 3, new Rect(0, 2, 4, 1));
        r["B.Grid"] = Tiled(ac, 48, 48, new Rect(0, 0, 48, 1), new Rect(0, 0, 1, 48));
        r["V.Scanlines"] = s.Scanlines && !t.Light ? Visibility.Visible : Visibility.Collapsed;

        r["F.Display"] = new FontFamily(t.DisplayFont);
        r["F.Body"] = new FontFamily(t.BodyFont);
        r["F.Mono"] = new FontFamily(t.MonoFont);

        r["Theme.Corners"] = t.Corners;
        r["Theme.Radius"] = Math.Clamp(s.CornerRadius, 0, 24);
        r["Theme.Light"] = t.Light;

        Changed?.Invoke();
    }

    private static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private static Brush Gradient(Color c, double[] alphas, double[] offsets, bool horizontal)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1) };
        for (int i = 0; i < alphas.Length; i++) g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * alphas[i]), c.R, c.G, c.B), offsets[i]));
        g.Freeze();
        return g;
    }

    private static Brush Radial(Color c, double alpha, Point center, double rx, double ry)
    {
        var g = new RadialGradientBrush { Center = center, GradientOrigin = center, RadiusX = rx, RadiusY = ry };
        g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(255 * alpha), c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        g.Freeze();
        return g;
    }

    private static Brush Tiled(Color c, double w, double h, params Rect[] rects)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, w, h))));
        var gg = new GeometryGroup();
        foreach (var r in rects) gg.Children.Add(new RectangleGeometry(r));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(c), null, gg));
        var b = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None,
            Viewport = new Rect(0, 0, w, h), ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, w, h), ViewboxUnits = BrushMappingMode.Absolute,
        };
        b.Freeze();
        return b;
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>A hex colour, or the theme brush when empty / invalid.</summary>
    public static Brush BrushOr(string hex, string key, double opacity = 1)
    {
        if (NoteMarkup.ParseColor(hex) is { } c)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(c.A * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
        var t = Brush(key);
        if (opacity >= 0.999 || t is not SolidColorBrush sb) return t;
        var o = new SolidColorBrush(Color.FromArgb((byte)(sb.Color.A * opacity), sb.Color.R, sb.Color.G, sb.Color.B));
        o.Freeze();
        return o;
    }
}
