using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SFHud.Interop;

/// <summary>Parses and prints key combos ("Ctrl+Alt+H", "Alt+~").</summary>
public static class Gesture
{
    public static bool TryParse(string text, out ModifierKeys mods, out Key key)
    {
        mods = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (text.EndsWith("++")) parts.Add("+");
        foreach (var raw in parts)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; continue;
                case "alt": mods |= ModifierKeys.Alt; continue;
                case "shift": mods |= ModifierKeys.Shift; continue;
                case "win": case "windows": mods |= ModifierKeys.Windows; continue;
            }
            key = raw.ToLowerInvariant() switch
            {
                "~" or "`" => Key.Oem3,
                "[" => Key.OemOpenBrackets,
                "]" => Key.OemCloseBrackets,
                "," => Key.OemComma,
                "." => Key.OemPeriod,
                "/" => Key.OemQuestion,
                ";" => Key.OemSemicolon,
                "'" => Key.OemQuotes,
                "-" => Key.OemMinus,
                "=" or "+" => Key.OemPlus,
                "\\" => Key.OemPipe,
                "backspace" or "back" => Key.Back,
                "space" => Key.Space,
                "esc" => Key.Escape,
                "pgup" => Key.PageUp,
                "pgdn" => Key.PageDown,
                _ when raw.Length == 1 && char.IsDigit(raw[0]) => Key.D0 + (raw[0] - '0'),
                _ => Enum.TryParse<Key>(raw, true, out var k) ? k : Key.None,
            };
        }
        return key != Key.None;
    }

    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key key) => key switch
    {
        Key.Oem3 => "~",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemPipe => "\\",
        Key.Back => "Backspace",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        _ => key.ToString(),
    };

    /// <summary>Canonical form for comparing two combos.</summary>
    public static string Normalize(string text) => TryParse(text, out var m, out var k) ? Format(m, k) : "";

    public static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}

/// <summary>
/// Thin wrapper over RegisterHotKey (no keyboard hooks, so game input is untouched).
/// A failed registration means another app already owns that combo.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly HashSet<int> _armed = new();
    private int _nextId = 100;

    public HotkeyManager(Window owner)
    {
        IntPtr handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("No window handle for hotkeys.");
        _source.AddHook(WndProc);
    }

    /// <summary>Registers a combo; returns its id, or -1 when Windows refused (taken by another app).</summary>
    public int Register(ModifierKeys mods, Key key, Action action)
    {
        int id = _nextId++;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!NativeMethods.RegisterHotKey(_source.Handle, id, (uint)mods | NativeMethods.MOD_NOREPEAT, vk)) return -1;
        _actions[id] = action;
        _armed.Add(id);
        return id;
    }

    public void Unregister(int id)
    {
        if (!_armed.Remove(id)) return;
        NativeMethods.UnregisterHotKey(_source.Handle, id);
        _actions.Remove(id);
    }

    public void UnregisterAll()
    {
        foreach (int id in _armed.ToList()) Unregister(id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;
        if (_actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            try { a(); }
            catch (Exception ex) { Services.Log.Error("hotkey action", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }
}
