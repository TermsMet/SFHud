using System;
using System.Windows;
using System.Windows.Media;
using SFHud.Services;

namespace SFHud.Controls;

/// <summary>
/// Builds every crosshair style as one frozen geometry centered in a (size + pad) square,
/// so the overlay, the settings preview and the Alt+~ editor all draw exactly the same shape.
/// </summary>
public static class CrosshairGeometry
{
    public const double Pad = 8;

    public static Geometry Build(CrosshairStyle style, double size, double gapSetting, out bool filled, bool centerDot = false)
    {
        size = Math.Clamp(size, 6, 64);
        double c = (size + Pad) / 2, h = size / 2;
        double gap = gapSetting >= 0 ? Math.Min(gapSetting, h - 1) : Math.Max(2, size * 0.18);
        filled = false;
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };

        void Line(double x1, double y1, double x2, double y2) => g.Children.Add(new LineGeometry(new Point(c + x1, c + y1), new Point(c + x2, c + y2)));
        void Dot(double r) { g.Children.Add(new EllipseGeometry(new Point(c, c), r, r)); }
        void Poly(bool closed, params double[] xy)
        {
            var fig = new PathFigure { StartPoint = new Point(c + xy[0], c + xy[1]), IsClosed = closed };
            for (int i = 2; i < xy.Length; i += 2) fig.Segments.Add(new LineSegment(new Point(c + xy[i], c + xy[i + 1]), true));
            g.Children.Add(new PathGeometry(new[] { fig }));
        }
        void Cross(bool top = true)
        {
            Line(-h, 0, -gap, 0);
            Line(gap, 0, h, 0);
            if (top) Line(0, -h, 0, -gap);
            Line(0, gap, 0, h);
        }

        switch (style)
        {
            case CrosshairStyle.Dot:
                filled = true;
                Dot(Math.Max(1.5, size / 7));
                break;
            case CrosshairStyle.Circle:
                g.Children.Add(new EllipseGeometry(new Point(c, c), h, h));
                Dot(1);
                break;
            case CrosshairStyle.T:
                Cross(top: false);
                break;
            case CrosshairStyle.CrossDot:
                Cross();
                Dot(1.2);
                break;
            case CrosshairStyle.CircleDot:
                filled = true;
                var ring = new EllipseGeometry(new Point(c, c), h, h);
                var hole = new EllipseGeometry(new Point(c, c), h - 1.6, h - 1.6);
                g.Children.Add(new CombinedGeometry(GeometryCombineMode.Exclude, ring, hole));
                Dot(Math.Max(1.5, size / 9));
                break;
            case CrosshairStyle.Chevron:
                Poly(false, -h, h * 0.55, 0, -h * 0.25, h, h * 0.55);
                break;
            case CrosshairStyle.Square:
                Poly(true, -h, -h, h, -h, h, h, -h, h);
                Dot(1);
                break;
            case CrosshairStyle.Diamond:
                Poly(true, 0, -h, h, 0, 0, h, -h, 0);
                Dot(1);
                break;
            case CrosshairStyle.Brackets:
                double k = h * 0.45;
                Poly(false, -h + k, -h, -h, -h, -h, -h + k);
                Poly(false, h - k, -h, h, -h, h, -h + k);
                Poly(false, -h + k, h, -h, h, -h, h - k);
                Poly(false, h - k, h, h, h, h, h - k);
                Dot(1);
                break;
            case CrosshairStyle.Triangle:
                Poly(true, 0, -h, h * 0.87, h * 0.5, -h * 0.87, h * 0.5);
                break;
            case CrosshairStyle.X:
                double d = h * 0.707, gd = gap * 0.707;
                Line(-d, -d, -gd, -gd);
                Line(d, -d, gd, -gd);
                Line(-d, d, -gd, gd);
                Line(d, d, gd, gd);
                break;
            default:
                Cross();
                break;
        }
        if (centerDot && style is not (CrosshairStyle.Dot or CrosshairStyle.CircleDot or CrosshairStyle.CrossDot)) Dot(Math.Max(1, size / 14));
        g.Freeze();
        return g;
    }

    public static Brush Brush(string hex, double opacity = 1)
    {
        Color color;
        try { color = (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { color = Color.FromRgb(0x00, 0xE5, 0xFF); }
        var b = new SolidColorBrush(color) { Opacity = Math.Clamp(opacity, 0.1, 1) };
        b.Freeze();
        return b;
    }

    /// <summary>Applies the user's crosshair to a shadow + main path pair inside a square container.</summary>
    public static void Apply(System.Windows.Shapes.Path main, System.Windows.Shapes.Path shadow, FrameworkElement container, AppSettings s)
    {
        var geo = Build(s.CrosshairStyle, s.CrosshairSize, s.CrosshairGap, out bool filled, s.CrosshairCenterDot);
        var brush = Brush(s.CrosshairColor, s.CrosshairOpacity);
        double t = Math.Clamp(s.CrosshairThickness, 1, 6);
        main.Data = geo;
        main.Stroke = brush;
        main.StrokeThickness = t;
        main.Fill = filled ? brush : null;
        shadow.Data = geo;
        shadow.Stroke = Brush(s.CrosshairOutlineColor, s.CrosshairOpacity);
        shadow.StrokeThickness = t + 2 * Math.Clamp(s.CrosshairOutlineThickness, 0.5, 4);
        shadow.Fill = filled ? shadow.Stroke : null;
        shadow.Visibility = s.CrosshairOutline ? Visibility.Visible : Visibility.Collapsed;
        container.Width = container.Height = Math.Clamp(s.CrosshairSize, 6, 64) + Pad;
    }
}
