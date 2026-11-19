using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SFHud.Controls;
using SFHud.Services;
using static SFHud.Interop.NativeMethods;

namespace SFHud.Interop;

/// <summary>
/// Gives an opaque borderless window the same outline as the <see cref="HudFrame"/> drawn inside it,
/// so nothing of the window shows past the cut corners. Chamfered themes get a window region with
/// the two cut corners; rounded themes use Windows 11's own rounded corners; square themes stay
/// square. Windows' 1 px accent border is turned off because the frame draws its own outline.
/// </summary>
public static class WindowShape
{
    public static void Attach(Window w, double chamfer)
    {
        void Apply() => Update(w, chamfer);
        w.SourceInitialized += (_, _) => Apply();
        w.SizeChanged += (_, _) => Apply();
        w.StateChanged += (_, _) => Apply();
        w.DpiChanged += (_, _) => Apply();
        ThemeManager.Changed += () => w.Dispatcher.BeginInvoke(Apply);
    }

    public static HudCorners CurrentCorners =>
        Application.Current?.TryFindResource("Theme.Corners") is HudCorners c ? c : HudCorners.Chamfer;

    private static void Update(Window w, double chamfer)
    {
        IntPtr hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        var corners = CurrentCorners;
        bool max = w.WindowState == WindowState.Maximized;

        int pref = corners == HudCorners.Rounded && !max ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        int none = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));

        if (max || corners != HudCorners.Chamfer || !GetWindowRect(hwnd, out RECT r) || r.Width <= 0)
        {
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        double scale = VisualTreeHelper.GetDpi(w).DpiScaleX;
        int width = r.Width, height = r.Height;
        int c = (int)Math.Round(Math.Min(chamfer, Math.Min(w.ActualWidth, w.ActualHeight) / 2) * scale);
        // Top-left and bottom-right are cut, the same pair HudFrame cuts.
        var pts = new[]
        {
            new GDIPOINT(c, 0), new GDIPOINT(width, 0), new GDIPOINT(width, height - c),
            new GDIPOINT(width - c, height), new GDIPOINT(0, height), new GDIPOINT(0, c),
        };
        IntPtr rgn = CreatePolygonRgn(pts, pts.Length, 2 /* WINDING */);
        if (rgn == IntPtr.Zero) return;
        if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn);
    }
}
