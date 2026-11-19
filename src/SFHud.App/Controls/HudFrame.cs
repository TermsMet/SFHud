using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SFHud.Services;

namespace SFHud.Controls;

/// <summary>
/// Chamfered sci-fi panel. Draws a cut-corner outline, optional corner brackets, a short accent tab
/// on the top edge, and a glow faked with wide low-alpha strokes (no blur effects, cheap to render
/// over a game). Content is laid out inside <see cref="Control.Padding"/>.
/// </summary>
public class HudFrame : ContentControl
{
    public static readonly DependencyProperty ChamferProperty = Reg(nameof(Chamfer), 12.0);
    public static readonly DependencyProperty ChamferAllProperty = Reg(nameof(ChamferAll), false);
    public static readonly DependencyProperty FillProperty = Reg<Brush?>(nameof(Fill), null);
    public static readonly DependencyProperty StrokeProperty = Reg<Brush?>(nameof(Stroke), null);
    public static readonly DependencyProperty StrokeThicknessProperty = Reg(nameof(StrokeThickness), 1.0);
    public static readonly DependencyProperty ShowBracketsProperty = Reg(nameof(ShowBrackets), true);
    public static readonly DependencyProperty BracketBrushProperty = Reg<Brush?>(nameof(BracketBrush), null);
    public static readonly DependencyProperty BracketLengthProperty = Reg(nameof(BracketLength), 16.0);
    public static readonly DependencyProperty ShowTabProperty = Reg(nameof(ShowTab), false);
    public static readonly DependencyProperty TabBrushProperty = Reg<Brush?>(nameof(TabBrush), null);
    public static readonly DependencyProperty GlowProperty = Reg(nameof(Glow), false);
    public static readonly DependencyProperty CornersProperty = Reg(nameof(Corners), HudCorners.Chamfer);
    public static readonly DependencyProperty RadiusProperty = Reg(nameof(Radius), 8.0);
    public static readonly DependencyProperty ClipContentProperty = DependencyProperty.Register(
        nameof(ClipContent), typeof(bool), typeof(HudFrame),
        new FrameworkPropertyMetadata(false, (d, _) => ((HudFrame)d).UpdateClip()));

    /// <summary>Theme corner style; the default style binds it to the Theme.Corners resource.</summary>
    public HudCorners Corners { get => (HudCorners)GetValue(CornersProperty); set => SetValue(CornersProperty, value); }
    public double Radius { get => (double)GetValue(RadiusProperty); set => SetValue(RadiusProperty, value); }
    public double Chamfer { get => (double)GetValue(ChamferProperty); set => SetValue(ChamferProperty, value); }
    /// <summary>Cut all four corners instead of the top-left / bottom-right pair.</summary>
    public bool ChamferAll { get => (bool)GetValue(ChamferAllProperty); set => SetValue(ChamferAllProperty, value); }
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public bool ShowBrackets { get => (bool)GetValue(ShowBracketsProperty); set => SetValue(ShowBracketsProperty, value); }
    public Brush? BracketBrush { get => (Brush?)GetValue(BracketBrushProperty); set => SetValue(BracketBrushProperty, value); }
    public double BracketLength { get => (double)GetValue(BracketLengthProperty); set => SetValue(BracketLengthProperty, value); }
    public bool ShowTab { get => (bool)GetValue(ShowTabProperty); set => SetValue(ShowTabProperty, value); }
    public Brush? TabBrush { get => (Brush?)GetValue(TabBrushProperty); set => SetValue(TabBrushProperty, value); }
    public bool Glow { get => (bool)GetValue(GlowProperty); set => SetValue(GlowProperty, value); }
    /// <summary>Clip this element and its content to the chamfered outline.</summary>
    public bool ClipContent { get => (bool)GetValue(ClipContentProperty); set => SetValue(ClipContentProperty, value); }

    private static DependencyProperty Reg<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(HudFrame),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateClip();
    }

    private void UpdateClip()
    {
        Clip = ClipContent && ActualWidth > 0 ? BuildOutline(new Size(ActualWidth, ActualHeight), 0) : null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        if (size.Width <= 0 || size.Height <= 0) return;

        double t = StrokeThickness;
        var outline = BuildOutline(size, t / 2);

        if (Fill is not null)
            dc.DrawGeometry(Fill, null, outline);

        if (Stroke is not null)
        {
            if (Glow && Stroke is SolidColorBrush scb)
            {
                dc.DrawGeometry(null, MakePen(scb.Color, 0.07, t + 7), outline);
                dc.DrawGeometry(null, MakePen(scb.Color, 0.16, t + 3), outline);
            }
            dc.DrawGeometry(null, new Pen(Stroke, t), outline);
        }

        double c = EffectiveChamfer(size);
        double l = t / 2, top = t / 2, r = size.Width - t / 2, b = size.Height - t / 2;

        if (ShowTab)
        {
            var tabPen = new Pen(TabBrush ?? BracketBrush ?? Stroke, 3) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            double x0 = l + c + 10, len = Math.Min(64, Math.Max(0, size.Width * 0.18));
            dc.DrawLine(tabPen, new Point(x0, top + 1), new Point(x0 + len, top + 1));
        }

        if (ShowBrackets && !ChamferAll && Corners == HudCorners.Chamfer)
        {
            var bp = new Pen(BracketBrush ?? Stroke, 2) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter };
            double len = Math.Min(BracketLength, Math.Min(size.Width, size.Height) / 3);
            // Top-right and bottom-left are the square corners when only two are chamfered.
            DrawCorner(dc, bp, new Point(r - len, top + 0.5), new Point(r - 0.5, top + 0.5), new Point(r - 0.5, top + len));
            DrawCorner(dc, bp, new Point(l + 0.5, b - len), new Point(l + 0.5, b - 0.5), new Point(l + len, b - 0.5));
        }
    }

    private static void DrawCorner(DrawingContext dc, Pen pen, Point a, Point corner, Point b)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a, false, false);
            ctx.LineTo(corner, true, false);
            ctx.LineTo(b, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }

    private static Pen MakePen(Color c, double alpha, double thickness)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(c.A * alpha), c.R, c.G, c.B));
        brush.Freeze();
        var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Miter };
        pen.Freeze();
        return pen;
    }

    private double EffectiveChamfer(Size s) => Math.Max(0, Math.Min(Chamfer, Math.Min(s.Width, s.Height) / 2));

    private Geometry BuildOutline(Size s, double inset)
    {
        if (Corners != HudCorners.Chamfer)
        {
            var rect = new Rect(inset, inset, Math.Max(0, s.Width - 2 * inset), Math.Max(0, s.Height - 2 * inset));
            double rad = Corners == HudCorners.Rounded ? Math.Min(Math.Min(Radius, Chamfer + 2), Math.Min(rect.Width, rect.Height) / 2) : 0;
            var rg = new RectangleGeometry(rect, rad, rad);
            rg.Freeze();
            return rg;
        }
        double c = EffectiveChamfer(s);
        double l = inset, t = inset, r = s.Width - inset, b = s.Height - inset;

        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(l + c, t), true, true);
            if (ChamferAll)
            {
                ctx.LineTo(new Point(r - c, t), true, false);
                ctx.LineTo(new Point(r, t + c), true, false);
            }
            else
            {
                ctx.LineTo(new Point(r, t), true, false);
            }
            ctx.LineTo(new Point(r, b - c), true, false);
            ctx.LineTo(new Point(r - c, b), true, false);
            if (ChamferAll)
            {
                ctx.LineTo(new Point(l + c, b), true, false);
                ctx.LineTo(new Point(l, b - c), true, false);
            }
            else
            {
                ctx.LineTo(new Point(l, b), true, false);
            }
            ctx.LineTo(new Point(l, t + c), true, false);
        }
        g.Freeze();
        return g;
    }
}
