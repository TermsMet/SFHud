using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SFHud.Services;

namespace SFHud.Interop;

/// <summary>
/// Which monitor SFHud's pop-up windows (note pads, quick note, timer prompt, AI) belong on: the one the
/// game is on. Without a running game it falls back to the monitor the window is already on, then the primary.
/// Rects are in the window's own DIPs, ready for Left / Top / Width / Height.
/// </summary>
internal static class GameScreen
{
    private static GameTarget Target => GameCatalog.Get(DataStore.Settings);

    /// <summary>Work area of the game's monitor (or the fallback), in <paramref name="w"/>'s DIPs.</summary>
    public static Rect GameWorkArea(Window w, GameTarget? target = null)
    {
        IntPtr game = IntPtr.Zero;
        try { game = GameLocator.Find(target ?? Target); } catch (Exception) { }
        return WorkAreaOf(game != IntPtr.Zero ? game : HandleOf(w), w);
    }

    /// <summary>Work area of the monitor <paramref name="w"/> is on now (for saving its position).</summary>
    public static Rect OwnWorkArea(Window w) => WorkAreaOf(HandleOf(w), w);

    /// <summary>Centres <paramref name="w"/> on the game's monitor (call from Loaded, once its size is known).</summary>
    public static void CenterOnGame(Window w, GameTarget? target = null)
    {
        var wa = GameWorkArea(w, target);
        double width = w.ActualWidth > 0 ? w.ActualWidth : w.Width, height = w.ActualHeight > 0 ? w.ActualHeight : w.Height;
        if (double.IsNaN(width) || double.IsNaN(height)) return;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = wa.Left + Math.Max(0, (wa.Width - width) / 2);
        w.Top = wa.Top + Math.Max(0, (wa.Height - height) / 2);
    }

    private static IntPtr HandleOf(Window w)
    {
        try { return new WindowInteropHelper(w).Handle; } catch (Exception) { return IntPtr.Zero; }
    }

    private static Rect WorkAreaOf(IntPtr hwnd, Window w)
    {
        if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;
        IntPtr mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (mon == IntPtr.Zero || !NativeMethods.GetMonitorInfo(mon, ref mi)) return SystemParameters.WorkArea;
        // WPF places a per-monitor-aware window using the DPI of the monitor it is on right now.
        var dpi = VisualTreeHelper.GetDpi(w);
        double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1, sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
        var r = mi.rcWork;
        return new Rect(r.Left / sx, r.Top / sy, r.Width / sx, r.Height / sy);
    }
}
