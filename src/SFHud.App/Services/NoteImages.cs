using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SFHud.Services;

/// <summary>
/// Images inside notes. Each image is copied into the data folder's "attachments" directory under a
/// content hash, and a note refers to it with one line:  ![caption](name.png =320)
/// (" =320" is the width in 1080p units; leave it out to fit the note's width).
/// Pasting the same picture twice reuses the same file. Nothing is uploaded anywhere.
/// </summary>
public static partial class NoteImages
{
    public static string Dir => Path.Combine(SettingsStore.DataDir, "attachments");
    public const long MaxBytes = 20 * 1024 * 1024;
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" };
    public const string FileFilter = "Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp";

    [GeneratedRegex(@"^\s*!\[([^\]\n]*)\]\(([A-Za-z0-9_\-]+\.(?:png|jpe?g|gif|bmp|webp))(?:\s*=\s*(\d{1,4}))?\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LineRx();

    public sealed record ImageRef(string Caption, string File, double Width);

    public static ImageRef? Parse(string line)
    {
        var m = LineRx().Match(line);
        if (!m.Success) return null;
        double w = int.TryParse(m.Groups[3].Value, out int n) ? Math.Clamp(n, 40, 4000) : 0;
        return new ImageRef(m.Groups[1].Value, m.Groups[2].Value, w);
    }

    public static bool IsImageLine(string line) => LineRx().IsMatch(line);

    public static string Line(string file, double width = 0, string caption = "") =>
        $"![{caption.Replace("]", "").Replace("\n", " ")}]({file}{(width > 0 ? $" ={Math.Round(width):0}" : "")})";

    public static string PathOf(string file) => Path.Combine(Dir, Path.GetFileName(file));

    /// <summary>Copies an image file into the attachments folder. Returns the markup line, or throws with a readable message.</summary>
    public static string Import(string source, double width = 0)
    {
        var info = new FileInfo(source);
        if (!info.Exists) throw new FileNotFoundException("That image no longer exists.", source);
        string ext = info.Extension.ToLowerInvariant();
        if (!Extensions.Contains(ext)) throw new InvalidDataException("Only PNG, JPG, GIF, BMP and WebP images can go in notes.");
        if (info.Length > MaxBytes) throw new InvalidDataException("That image is over 20 MB.");
        byte[] bytes = File.ReadAllBytes(source);
        if (Load(bytes) is null) throw new InvalidDataException("That file couldn't be read as an image.");
        return Line(Store(bytes, ext == ".jpeg" ? ".jpg" : ext), width, Path.GetFileNameWithoutExtension(source));
    }

    /// <summary>Saves the clipboard picture (screenshots, Snipping Tool, browsers) as a PNG. Null if there's no image.</summary>
    public static string? FromClipboard(double width = 0)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var f = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(p => Extensions.Contains(Path.GetExtension(p).ToLowerInvariant()));
                if (f is not null) return Import(f, width);
            }
            if (!Clipboard.ContainsImage()) return null;
            var src = Clipboard.GetImage();
            if (src is null) return null;
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return Line(Store(ms.ToArray(), ".png"), width, "pasted " + DateTime.Now.ToString("HH:mm"));
        }
        catch (Exception ex) when (ex is not InvalidDataException) { Log.Warn("paste image: " + ex.Message); return null; }
    }

    public static bool ClipboardHasImage()
    {
        try
        {
            return Clipboard.ContainsImage() ||
                   (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().Any(p => Extensions.Contains(Path.GetExtension(p).ToLowerInvariant())));
        }
        catch (Exception) { return false; }
    }

    private static string Store(byte[] bytes, string ext)
    {
        Directory.CreateDirectory(Dir);
        string name = Convert.ToHexString(SHA256.HashData(bytes))[..20].ToLowerInvariant() + ext;
        string path = Path.Combine(Dir, name);
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        return name;
    }

    // ---------------------------------------------------------------- loading (cached, decoded small)

    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? Get(string file)
    {
        return Cache.GetOrAdd(file, f =>
        {
            try
            {
                string p = PathOf(f);
                return File.Exists(p) ? Load(File.ReadAllBytes(p)) : null;
            }
            catch (Exception ex) { Log.Warn("note image: " + ex.Message); return null; }
        });
    }

    private static BitmapSource? Load(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            BitmapSource result = bmp;
            if (bmp.PixelWidth > 2560)
            {
                double s = 2560.0 / bmp.PixelWidth;
                result = new TransformedBitmap(bmp, new System.Windows.Media.ScaleTransform(s, s));
            }
            result.Freeze();
            return result;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Changes the width of the image on one line of a note's text.</summary>
    public static string Resize(string text, int lineIndex, double width)
    {
        var lines = text.Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length || Parse(lines[lineIndex].TrimEnd('\r')) is not { } img) return text;
        lines[lineIndex] = Line(img.File, width, img.Caption);
        return string.Join("\n", lines);
    }

    public static string RemoveLine(string text, int lineIndex)
    {
        var lines = text.Split('\n').ToList();
        if (lineIndex < 0 || lineIndex >= lines.Count) return text;
        lines.RemoveAt(lineIndex);
        return string.Join("\n", lines);
    }
}
