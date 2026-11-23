using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using SFHud.Interop;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using static SFHud.Interop.NativeMethods;

namespace SFHud.Services;

/// <summary>
/// Reads text from a small region of the game window with Windows' built-in OCR engine
/// (Windows.Media.Ocr, fully offline). It copies screen pixels the same way a screenshot tool does:
/// no game memory, no injection. Exclusive-fullscreen games may capture as black; use borderless.
/// </summary>
public static class OcrService
{
    private static OcrEngine? _engine;

    public static bool Available => (_engine ??= OcrEngine.TryCreateFromUserProfileLanguages()) is not null;

    /// <summary>Region is in fractions of the game's client area.</summary>
    public static async Task<string?> ReadRegionAsync(IntPtr game, double fx, double fy, double fw, double fh)
    {
        if (!Available || game == IntPtr.Zero) return null;
        if (!GetClientRect(game, out RECT client) || client.Width <= 0) return null;
        var origin = new POINT();
        ClientToScreen(game, ref origin);
        int x = origin.X + (int)(client.Width * Math.Clamp(fx, 0, 1));
        int y = origin.Y + (int)(client.Height * Math.Clamp(fy, 0, 1));
        int w = Math.Max(8, (int)(client.Width * Math.Clamp(fw, 0.01, 1)));
        int h = Math.Max(8, (int)(client.Height * Math.Clamp(fh, 0.01, 1)));
        return await ReadScreenAsync(x, y, w, h);
    }

    public static async Task<string?> ReadScreenAsync(int x, int y, int w, int h)
    {
        byte[]? px = Capture(x, y, w, h);
        if (px is null) return null;
        // Small HUD text reads far better at 2-3x.
        int scale = h < 40 ? 3 : h < 90 ? 2 : 1;
        if (scale > 1) (px, w, h) = Upscale(px, w, h, scale);
        try
        {
            using var bmp = SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
            var result = await _engine!.RecognizeAsync(bmp);
            return result.Text;
        }
        catch (Exception ex)
        {
            Log.Warn("ocr: " + ex.Message);
            return null;
        }
    }

    private static byte[]? Capture(int x, int y, int w, int h)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY)) return null;
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var buf = new byte[w * h * 4];
            SelectObject(mem, old);
            if (GetDIBits(mem, bmp, 0, (uint)h, buf, ref bi, 0) == 0) return null;
            for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;
            return buf;
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static (byte[], int, int) Upscale(byte[] src, int w, int h, int s)
    {
        int W = w * s, H = h * s;
        var dst = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            int sy = y / s;
            for (int x = 0; x < W; x++)
                Buffer.BlockCopy(src, (sy * w + x / s) * 4, dst, (y * W + x) * 4, 4);
        }
        return (dst, W, H);
    }
}
