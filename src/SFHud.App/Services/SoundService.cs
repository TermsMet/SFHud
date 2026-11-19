using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;

namespace SFHud.Services;

public sealed record SoundInfo(string Id, string Name, string Path, bool BuiltIn);

/// <summary>
/// Timer and notification sounds. The built-in set is synthesized by SFHud itself (soft sine tones
/// with smooth envelopes), so it has no third-party license strings attached. Users can import
/// their own .wav / .mp3 files (for example free sounds from Pixabay) into sounds\custom.
/// </summary>
public static class SoundService
{
    private const int Rate = 44100;
    private static readonly List<MediaPlayer> Playing = new();

    private static readonly (string Id, string Name, Func<float[]> Make)[] Synths =
    {
        ("soft-chime", "Soft chime", SoftChime),
        ("gentle-beep", "Gentle beep ×3", GentleBeep),
        ("glass-ding", "Glass ding", GlassDing),
        ("marimba", "Marimba up", Marimba),
        ("soft-bell", "Soft bell", SoftBell),
        ("bubble", "Bubble pop", Bubble),
        ("radio-blip", "Radio blip", RadioBlip),
        ("low-pulse", "Low pulse", LowPulse),
        ("kalimba", "Kalimba rise", Kalimba),
        ("gentle-alarm", "Gentle alarm", GentleAlarm),
    };

    public static string BuiltInDir => Path.Combine(SettingsStore.SoundsDir, "builtin");
    public static string CustomDir => Path.Combine(SettingsStore.SoundsDir, "custom");

    public const string PixabaySearch = "https://pixabay.com/sound-effects/search/soft%20beep/";

    /// <summary>Writes the built-in WAVs if they are missing (first run, or the folder was cleaned).</summary>
    public static void EnsureBuiltIns()
    {
        try
        {
            Directory.CreateDirectory(BuiltInDir);
            Directory.CreateDirectory(CustomDir);
            foreach (var (id, _, make) in Synths)
            {
                string p = Path.Combine(BuiltInDir, id + ".wav");
                if (!File.Exists(p)) WriteWav(p, make());
            }
        }
        catch (Exception ex) { Log.Error("sounds", ex); }
    }

    public static List<SoundInfo> All()
    {
        var list = Synths.Select(s => new SoundInfo("builtin:" + s.Id, s.Name, Path.Combine(BuiltInDir, s.Id + ".wav"), true)).ToList();
        try
        {
            if (Directory.Exists(CustomDir))
                foreach (var f in Directory.GetFiles(CustomDir).Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                    list.Add(new SoundInfo("custom:" + Path.GetFileName(f), Path.GetFileNameWithoutExtension(f) + " (yours)", f, false));
        }
        catch (Exception) { }
        return list;
    }

    public static string NameOf(string id) => id.Length == 0 ? "None" : All().FirstOrDefault(s => s.Id == id)?.Name ?? id;

    public static string Import(string file)
    {
        Directory.CreateDirectory(CustomDir);
        string name = Path.GetFileName(file);
        string dest = Path.Combine(CustomDir, name);
        File.Copy(file, dest, overwrite: true);
        return "custom:" + name;
    }

    public static void Delete(string id)
    {
        var s = All().FirstOrDefault(x => x.Id == id);
        if (s is { BuiltIn: false } && File.Exists(s.Path)) File.Delete(s.Path);
    }

    /// <summary>Plays a sound by id, respecting mute and volume. "" plays nothing.</summary>
    public static void Play(string id, bool force = false)
    {
        var s = DataStore.Settings;
        if (id.Length == 0 || (s.SoundMuted && !force)) return;
        var info = All().FirstOrDefault(x => x.Id == id);
        if (info is null || !File.Exists(info.Path)) { System.Media.SystemSounds.Asterisk.Play(); return; }
        try
        {
            var p = new MediaPlayer { Volume = Math.Clamp(s.SoundVolume, 0, 1) };
            p.MediaEnded += (_, _) => { p.Close(); Playing.Remove(p); };
            p.MediaFailed += (_, _) => { p.Close(); Playing.Remove(p); };
            p.Open(new Uri(info.Path));
            p.Play();
            Playing.Add(p); // keep a reference until it ends
            while (Playing.Count > 6) { Playing[0].Close(); Playing.RemoveAt(0); }
        }
        catch (Exception ex) { Log.Warn("sound: " + ex.Message); }
    }

    // ================================================================ synthesis

    private static float[] Buffer(double seconds) => new float[(int)(Rate * seconds)];

    /// <summary>Adds a sine note with a fast attack and exponential decay (soft, click-free).</summary>
    private static void Note(float[] buf, double start, double freq, double amp, double decay, double length, double attack = 0.006, params (double Mult, double Amp)[] partials)
    {
        int s0 = (int)(start * Rate), n = (int)(length * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double t = i / (double)Rate;
            double env = Math.Min(1, t / attack) * Math.Exp(-t / decay);
            double tail = Math.Min(1, (n - i) / (Rate * 0.02)); // 20 ms fade at the end
            double v = Math.Sin(2 * Math.PI * freq * t);
            foreach (var (m, a) in partials) v += a * Math.Sin(2 * Math.PI * freq * m * t);
            buf[s0 + i] += (float)(amp * env * tail * v);
        }
    }

    /// <summary>A beep with a flat sustain and soft edges.</summary>
    private static void Beep(float[] buf, double start, double freq, double amp, double length, double edge = 0.012)
    {
        int s0 = (int)(start * Rate), n = (int)(length * Rate), e = (int)(edge * Rate);
        for (int i = 0; i < n && s0 + i < buf.Length; i++)
        {
            double env = Math.Min(1, Math.Min(i / (double)e, (n - i) / (double)e));
            env = env * env * (3 - 2 * env);
            double t = i / (double)Rate;
            buf[s0 + i] += (float)(amp * env * (Math.Sin(2 * Math.PI * freq * t) + 0.12 * Math.Sin(4 * Math.PI * freq * t)));
        }
    }

    private static float[] SoftChime() { var b = Buffer(1.2); Note(b, 0, 1318.5, 0.32, 0.28, 1.0, 0.006, (2, 0.15)); Note(b, 0.16, 987.8, 0.3, 0.35, 1.0, 0.006, (2, 0.12)); return b; }
    private static float[] GentleBeep() { var b = Buffer(0.75); for (int i = 0; i < 3; i++) Beep(b, i * 0.22, 880, 0.26, 0.11); return b; }
    private static float[] GlassDing() { var b = Buffer(1.6); Note(b, 0, 1760, 0.26, 0.55, 1.6, 0.003, (1.5, 0.22), (2.0, 0.12), (3.0, 0.05)); return b; }
    private static float[] Marimba() { var b = Buffer(0.9); Note(b, 0, 523.25, 0.4, 0.16, 0.6, 0.003, (4, 0.18), (10, 0.04)); Note(b, 0.15, 783.99, 0.4, 0.2, 0.7, 0.003, (4, 0.18), (10, 0.04)); return b; }
    private static float[] SoftBell() { var b = Buffer(2.0); Note(b, 0, 659.25, 0.3, 0.7, 2.0, 0.004, (2, 0.4), (3, 0.18), (4.2, 0.08)); return b; }
    private static float[] Kalimba() { var b = Buffer(1.0); double[] f = { 880, 1046.5, 1318.5 }; for (int i = 0; i < 3; i++) Note(b, i * 0.11, f[i], 0.3, 0.22, 0.7, 0.002, (3, 0.1)); return b; }
    private static float[] LowPulse() { var b = Buffer(0.7); Beep(b, 0, 330, 0.38, 0.18, 0.03); Beep(b, 0.3, 330, 0.38, 0.18, 0.03); return b; }
    private static float[] RadioBlip() { var b = Buffer(0.35); Beep(b, 0, 1400, 0.2, 0.05, 0.006); Beep(b, 0.1, 1850, 0.2, 0.05, 0.006); return b; }

    private static float[] Bubble()
    {
        var b = Buffer(0.45);
        foreach (double start in new[] { 0.0, 0.18 })
        {
            int s0 = (int)(start * Rate), n = (int)(0.11 * Rate);
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                double t = i / (double)n;
                double f = 450 + 700 * t * t;
                phase += 2 * Math.PI * f / Rate;
                double env = Math.Sin(Math.PI * t);
                b[s0 + i] += (float)(0.33 * env * Math.Sin(phase));
            }
        }
        return b;
    }

    private static float[] GentleAlarm()
    {
        var b = Buffer(1.75);
        for (int r = 0; r < 2; r++)
            for (int i = 0; i < 4; i++)
                Beep(b, r * 0.9 + i * 0.2, i % 2 == 0 ? 739.99 : 987.77, 0.24, 0.14);
        return b;
    }

    private static void WriteWav(string path, float[] samples)
    {
        float peak = samples.Select(Math.Abs).DefaultIfEmpty(1).Max();
        float gain = peak > 0.95f ? 0.95f / peak : 1f;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int bytes = samples.Length * 2;
        w.Write("RIFF"u8.ToArray()); w.Write(36 + bytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(bytes);
        foreach (var s in samples) w.Write((short)Math.Clamp(s * gain * 32767, -32768, 32767));
    }
}
