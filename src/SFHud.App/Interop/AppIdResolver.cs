using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SFHud.Services;

namespace SFHud.Interop;

/// <summary>
/// Maps a Windows AppUserModelID (what media sessions report) to the process that owns it, by reading
/// the System.AppUserModel.ID property of top-level windows. Needed because some apps report an
/// anonymous hash (e.g. "83C1C0F3FA8524B1" for LibreWolf) instead of a readable name.
/// Read-only: it only reads window properties.
/// </summary>
internal static class AppIdResolver
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; [FieldOffset(16)] public IntPtr pad; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint c);
        int GetAt(uint i, out PROPERTYKEY k);
        int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        int SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr h, ref Guid iid, out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT v);

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc proc, IntPtr l);

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _scanned = DateTime.MinValue;
    /// <summary>Ids no window carried at the last scan (e.g. the Steam overlay's anonymous id): asked again only after a minute.</summary>
    private static readonly Dictionary<string, DateTime> Misses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Process name (no .exe) for an AppUserModelID, or null if no window carries it.</summary>
    public static string? ProcessFor(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        lock (Map)
        {
            if (Map.TryGetValue(aumid, out var hit)) return hit;
            if ((DateTime.UtcNow - _scanned).TotalSeconds < 5) return null; // don't rescan on every tick
            if (Misses.TryGetValue(aumid, out var missed) && (DateTime.UtcNow - missed).TotalSeconds < 60) return null;
            _scanned = DateTime.UtcNow;
            try { Scan(); } catch (Exception ex) { Log.Warn("aumid scan: " + ex.Message); }
            if (Map.TryGetValue(aumid, out hit)) { Misses.Remove(aumid); return hit; }
            if (Misses.Count > 64) Misses.Clear();
            Misses[aumid] = DateTime.UtcNow;
            return null;
        }
    }

    private static void Scan()
    {
        var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
        var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 }; // PKEY_AppUserModel_ID
        var names = new Dictionary<uint, string>();
        EnumWindows((h, _) =>
        {
            // Only visible top-level windows can own a media session's app id; skipping the hundreds of
            // hidden ones avoids a COM property-store call per window.
            if (!NativeMethods.IsWindowVisible(h)) return true;
            if (SHGetPropertyStoreForWindow(h, ref iid, out var store) != 0 || store is null) return true;
            try
            {
                if (store.GetValue(ref key, out var v) == 0)
                {
                    try
                    {
                        if (v.vt == 31 /* VT_LPWSTR */ && v.p != IntPtr.Zero && Marshal.PtrToStringUni(v.p) is { Length: > 0 } id && !Map.ContainsKey(id))
                        {
                            NativeMethods.GetWindowThreadProcessId(h, out uint pid);
                            if (!names.TryGetValue(pid, out var name))
                            {
                                name = GameLocator.ImageName(pid) ?? "";
                                names[pid] = name;
                            }
                            if (name.Length > 0) Map[id] = name;
                        }
                    }
                    finally { PropVariantClear(ref v); }
                }
            }
            finally { Marshal.ReleaseComObject(store); }
            return true;
        }, IntPtr.Zero);
    }
}
