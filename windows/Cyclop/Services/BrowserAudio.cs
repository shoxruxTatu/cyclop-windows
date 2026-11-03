using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Cyclop.Services;

/// <summary>
/// A browser that plays sound without telling Windows what it plays. Yandex
/// Browser publishes no media session at all — the flyout and the sessions
/// API see nothing while a video is plainly playing. Its sound still goes
/// through the mixer, its window still carries the tab's title, and it still
/// answers the media keys; that is enough for the music tab.
/// </summary>
internal static class BrowserAudio
{
    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "browser", "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
    };

    /// <summary>The process name of a browser making sound right now, on any output.</summary>
    public static string? Playing()
    {
        var names = new Dictionary<uint, string>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                if (enumerator.EnumAudioEndpoints(0 /* render */, 1 /* active */, out var devices) != 0) return null;
                try
                {
                    devices.GetCount(out var count);
                    for (uint i = 0; i < count; i++)
                    {
                        if (devices.Item(i, out var device) != 0) continue;
                        try
                        {
                            var found = PlayingOn(device, names);
                            if (found is not null) return found;
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }
                finally { Marshal.ReleaseComObject(devices); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch (Exception e) { Support.Log("browser audio: " + e.Message); }
        return null;
    }

    static string? PlayingOn(IMMDevice device, Dictionary<uint, string> names)
    {
        var iid = typeof(IAudioSessionManager2).GUID;
        if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var activated) != 0) return null;
        var manager = (IAudioSessionManager2)activated;
        try
        {
            if (manager.GetSessionEnumerator(out var sessions) != 0) return null;
            try
            {
                sessions.GetCount(out var count);
                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var session) != 0) continue;
                    try
                    {
                        if (session.GetState(out var state) != 0 || state != 1 /* active */) continue;
                        if (session.GetProcessId(out var pid) != 0 || pid == 0) continue;
                        if (!names.TryGetValue(pid, out var name))
                        {
                            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = ""; }
                            names[pid] = name;
                        }
                        if (Browsers.Contains(name)) return name;
                    }
                    finally { Marshal.ReleaseComObject(session); }
                }
            }
            finally { Marshal.ReleaseComObject(sessions); }
        }
        finally { Marshal.ReleaseComObject(manager); }
        return null;
    }

    public static bool Running(string process) => Process.GetProcessesByName(process).Length > 0;

    // \s, not a space: Yandex joins its two words with a no-break space.
    static readonly Regex BrowserSuffix = new(
        @"\s[—–-]\s(Яндекс\sБраузер|Yandex\sBrowser|Yandex|Google\sChrome|Mozilla\sFirefox|Opera|Brave|Vivaldi|.*Microsoft\sEdge)$",
        RegexOptions.IgnoreCase);
    static readonly Regex Counter = new(@"^(\d+:\s+)?(\(\d+\)\s+)?");
    static readonly string[] Sites = { "YouTube Music", "YouTube", "SoundCloud", "Twitch", "RUTUBE", "VK Видео", "ВКонтакте", "Яндекс Музыка" };

    /// <summary>"1: (44) Song - YouTube — Яндекс Браузер" → ("Song", "YouTube"):
    /// the browser's name, the unread counter and the site come off.</summary>
    public static (string Title, string Site) Clean(string text)
    {
        var title = BrowserSuffix.Replace(text.Trim(), "");
        title = Counter.Replace(title, "");
        foreach (var site in Sites)
        {
            foreach (var dash in new[] { " - ", " — ", " | " })
            {
                if (title.EndsWith(dash + site, StringComparison.OrdinalIgnoreCase) && title.Length > dash.Length + site.Length)
                    return (title[..^(dash.Length + site.Length)].Trim(), site);
            }
        }
        return (title.Trim(), "");
    }

    // MARK: - Core Audio

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr group, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr group, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    /// <summary>IAudioSessionControl's nine methods come first; only the two
    /// asked for are spelled out, the rest hold their places.</summary>
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        void GetDisplayName();
        void SetDisplayName();
        void GetIconPath();
        void SetIconPath();
        void GetGroupingParam();
        void SetGroupingParam();
        void RegisterAudioSessionNotification();
        void UnregisterAudioSessionNotification();
        void GetSessionIdentifier();
        void GetSessionInstanceIdentifier();
        [PreserveSig] int GetProcessId(out uint pid);
    }
}
