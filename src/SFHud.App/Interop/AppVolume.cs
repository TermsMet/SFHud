using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SFHud.Services;

namespace SFHud.Interop;

/// <summary>
/// Per-app volume through Windows Core Audio (the same sliders as the Volume Mixer). Changes only the
/// sessions of the given processes on the default output device, never the game or the master volume.
/// </summary>
internal static class AppVolume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        int GetSessionEnumerator(out IAudioSessionEnumerator e);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(string name, IntPtr ctx);
        int GetIconPath(out IntPtr path);
        int SetIconPath(string path, IntPtr ctx);
        int GetGroupingParam(out Guid g);
        int SetGroupingParam(ref Guid g, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr n);
        int UnregisterAudioSessionNotification(IntPtr n);
        // IAudioSessionControl2
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid ctx);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid ctx);
        int GetMute(out bool mute);
    }

    private static readonly Guid Ctx = new("6A2E4B1C-5F0D-4C47-9E1B-53464855440D"); // "SFHUD" event context

    /// <summary>Calls back with the volume interface of every output session owned by one of the processes.</summary>
    private static int ForEach(IReadOnlyCollection<string> processes, Action<ISimpleAudioVolume> act)
    {
        if (processes.Count == 0) return 0;
        int n = 0;
        object? en = null;
        IMMDeviceCollection? devices = null;
        var names = new Dictionary<uint, string>();
        try
        {
            en = new MMDeviceEnumeratorCom();
            // Every active output, not just the default one: players are often routed to another device.
            if (((IMMDeviceEnumerator)en).EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out devices) != 0 || devices is null) return 0;
            devices.GetCount(out int dc);
            for (int d = 0; d < dc; d++)
            {
                if (devices.Item(d, out var dev) != 0 || dev is null) continue;
                try { n += ForEachOn(dev, processes, names, act); }
                finally { Marshal.ReleaseComObject(dev); }
            }
        }
        catch (Exception ex) { Log.Warn("app volume: " + ex.Message); }
        finally
        {
            if (devices is not null) Marshal.ReleaseComObject(devices);
            if (en is not null) Marshal.ReleaseComObject(en);
        }
        return n;
    }

    private static int ForEachOn(IMMDevice dev, IReadOnlyCollection<string> processes, Dictionary<uint, string> names, Action<ISimpleAudioVolume> act)
    {
        int n = 0;
        object? mgrObj = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (dev.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out mgrObj) != 0) return 0;
            if (((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions) != 0) return 0;
            sessions.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var s) != 0 || s is null) continue;
                try
                {
                    if (s.GetProcessId(out uint pid) != 0 || pid == 0) continue;
                    if (!names.TryGetValue(pid, out var name)) names[pid] = name = System.IO.Path.GetFileNameWithoutExtension(GameLocator.ImageName(pid) ?? "").ToLowerInvariant();
                    if (!processes.Contains(name)) continue;
                    act((ISimpleAudioVolume)s);
                    n++;
                }
                finally { Marshal.ReleaseComObject(s); }
            }
        }
        finally
        {
            if (sessions is not null) Marshal.ReleaseComObject(sessions);
            if (mgrObj is not null) Marshal.ReleaseComObject(mgrObj);
        }
        return n;
    }

    /// <summary>Loudest session volume (0..1) of these processes, or null if none is making sound.</summary>
    public static double? Get(IReadOnlyCollection<string> processes)
    {
        float best = -1;
        ForEach(processes, v => { if (v.GetMasterVolume(out float l) == 0 && l > best) best = l; });
        return best < 0 ? null : best;
    }

    /// <summary>Sets every session of these processes; returns how many were changed.</summary>
    public static int Set(IReadOnlyCollection<string> processes, double level)
    {
        float l = (float)Math.Clamp(level, 0, 1);
        var ctx = Ctx;
        return ForEach(processes, v => { v.SetMasterVolume(l, ref ctx); if (l > 0) v.SetMute(false, ref ctx); });
    }
}
