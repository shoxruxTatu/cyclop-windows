using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Cyclop.Services;

/// <summary>
/// Finds the window a media session plays in and brings it forward — the
/// click on the artwork that takes you to the film instead of hunting for the
/// browser.
///
/// A session names its app by AppUserModelID, not by window. Windows carry the
/// same ID as a property when the app sets one (browsers, Store apps), so that
/// is asked first; failing that, the process name the ID points to. Of several
/// windows of one browser, the one whose title is the track's — the page
/// playing — wins, then the most recently used.
/// </summary>
internal static class SourceWindow
{
    public static bool Reveal(string appId, string? title)
    {
        try
        {
            var window = Find(appId, title);
            if (window != IntPtr.Zero)
            {
                Bring(window);
                return true;
            }
            // A Store app without a window to find opens through its ID.
            if (appId.Contains('!'))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}") { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception e) { Support.Log("reveal source failed: " + e.Message); }
        Support.Log($"reveal source: no window for {appId}");
        return false;
    }

    static IntPtr Find(string appId, string? title)
    {
        var names = ProcessNames(appId);
        var processNames = new Dictionary<uint, string>();
        IntPtr best = IntPtr.Zero;
        int bestScore = 0;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero || IsCloaked(hwnd)) return true;
            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            if (((long)GetWindowLongPtr(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;

            int score = 0;
            if (string.Equals(WindowAppId(hwnd), appId, StringComparison.OrdinalIgnoreCase)) score += 4;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!processNames.TryGetValue(pid, out var process))
            {
                try { process = Process.GetProcessById((int)pid).ProcessName; } catch { process = ""; }
                processNames[pid] = process;
            }
            if (names.Contains(process)) score += 2;
            if (score == 0) return true;
            if (!string.IsNullOrWhiteSpace(title))
            {
                var text = new StringBuilder(length + 1);
                GetWindowText(hwnd, text, text.Capacity);
                if (text.ToString().Contains(title, StringComparison.OrdinalIgnoreCase)) score += 1;
            }
            // Top-down Z order: on a tie, the window used last stays ahead.
            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    /// <summary>Process names an AppUserModelID may stand for.</summary>
    static HashSet<string> ProcessNames(string appId)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var id = appId.Trim();
        var lower = id.ToLowerInvariant();
        if (lower.EndsWith(".exe")) names.Add(System.IO.Path.GetFileNameWithoutExtension(id));
        if (lower.Contains("chrome")) names.Add("chrome");
        if (lower.Contains("msedge")) names.Add("msedge");
        if (lower.Contains("firefox") || lower == "308046b0af4a39cb") names.Add("firefox");
        if (lower.Contains("yandex")) names.Add("browser");
        if (lower.Contains("opera")) names.Add("opera");
        if (lower.Contains("brave")) names.Add("brave");
        if (lower.Contains("vivaldi")) names.Add("vivaldi");
        if (lower.Contains("arc")) names.Add("Arc");
        if (lower.Contains("spotify")) names.Add("Spotify");
        if (lower.Contains("vlc")) names.Add("vlc");
        if (lower.Contains("telegram")) names.Add("Telegram");
        // "Publisher.App_hash!Entry": both ends are worth a try.
        int bang = id.IndexOf('!');
        if (bang > 0 && bang < id.Length - 1) names.Add(id[(bang + 1)..]);
        var head = bang > 0 ? id[..bang] : id;
        int underscore = head.IndexOf('_');
        if (underscore > 0) head = head[..underscore];
        int dot = head.LastIndexOf('.');
        names.Add(dot >= 0 && dot < head.Length - 1 ? head[(dot + 1)..] : head);
        return names;
    }

    static void Bring(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (SetForegroundWindow(hwnd)) return;
        // Windows only lets the app that had the last input hand the
        // foreground on. A tap of Alt counts as that input.
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    // MARK: - The window's own AppUserModelID

    static readonly Guid PropertyStoreId = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey
    {
        public Guid Format;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant
    {
        public ushort Type;
        ushort reserved1, reserved2, reserved3;
        public IntPtr Value;
        IntPtr extra;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    static string? WindowAppId(IntPtr hwnd)
    {
        try
        {
            var iid = PropertyStoreId;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;
            try
            {
                var key = new PropertyKey { Format = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                if (store.GetValue(ref key, out var value) != 0) return null;
                try { return value.Type == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.Value) : null; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }

    /// <summary>Store apps keep hidden, "cloaked" windows around; they are not
    /// a place to bring anyone.</summary>
    static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const uint GW_OWNER = 4;
    const int SW_RESTORE = 9;
    const byte VK_MENU = 0x12;
    const uint KEYEVENTF_KEYUP = 0x2;

    delegate bool EnumProc(IntPtr hwnd, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("shell32.dll")] static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant value);
}
