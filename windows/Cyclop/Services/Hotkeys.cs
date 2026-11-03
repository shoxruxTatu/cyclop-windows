using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace Cyclop.Services;

/// <summary>A key with its modifiers, as written in config.json: "Ctrl+Alt+A".</summary>
internal readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": case "windows": modifiers |= ModifierKeys.Windows; break;
                default:
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (name.Equals("PrintScreen", StringComparison.OrdinalIgnoreCase)) name = "Snapshot";
                    if (Enum.TryParse<Key>(name, ignoreCase: true, out var k)) key = k;
                    break;
            }
        }
        return key is { } found && found != Key.None ? new Hotkey(modifiers, found) : null;
    }

    public override string ToString()
    {
        var mods = ModifiersText(Modifiers);
        return mods.Length > 0 ? mods + "+" + KeyName(Key) : KeyName(Key);
    }

    public static string ModifiersText(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }

    static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        Key.Snapshot => "PrintScreen",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        _ => key.ToString(),
    };

    /// <summary>Keys that are a shortcut on their own; everything else needs a
    /// modifier, or typing the letter anywhere would take a screenshot.</summary>
    public bool IsUsable =>
        Modifiers != ModifierKeys.None || Key is >= Key.F1 and <= Key.F24 or Key.Snapshot or Key.Pause or Key.Scroll;

    public static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System;
}

/// <summary>
/// System-wide shortcuts through <c>RegisterHotKey</c>, delivered to a hidden
/// message window. A combination another app already owns cannot be taken —
/// Windows refuses it, and the reason is kept for Settings to show.
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    readonly ConfigStore config;
    readonly HwndSource source;
    readonly Dictionary<int, Action> actions = new();
    readonly HashSet<int> registered = new();
    bool suspended;

    public Action? Area;
    public Action? Screen;

    /// <summary>Which shortcuts Windows refused, by id: 1 area, 2 screen.</summary>
    public HashSet<int> Refused { get; } = new();

    public HotkeyService(ConfigStore config)
    {
        this.config = config;
        source = new HwndSource(new HwndSourceParameters("CyclopHotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        source.AddHook(Hook);
        actions[1] = () => Area?.Invoke();
        actions[2] = () => Screen?.Invoke();
        Register();
    }

    public const int AreaId = 1, ScreenId = 2;

    public void Register()
    {
        Unregister();
        if (suspended) return;
        Refused.Clear();
        Bind(AreaId, config.HotkeyArea);
        Bind(ScreenId, config.HotkeyScreen);
    }

    void Bind(int id, string text)
    {
        if (Hotkey.Parse(text) is not { } hotkey) return;
        uint mods = MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= MOD_ALT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control)) mods |= MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= MOD_WIN;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        if (RegisterHotKey(source.Handle, id, mods, vk))
        {
            registered.Add(id);
            Support.Log($"hotkey {hotkey} registered");
        }
        else
        {
            Refused.Add(id);
            Support.Log($"hotkey {hotkey} is taken (error {Marshal.GetLastWin32Error()})");
        }
    }

    void Unregister()
    {
        foreach (var id in registered) UnregisterHotKey(source.Handle, id);
        registered.Clear();
    }

    /// <summary>While a new shortcut is being recorded, so pressing the
    /// current one records it instead of firing it.</summary>
    public void Suspend()
    {
        suspended = true;
        Unregister();
    }

    public void Resume()
    {
        suspended = false;
        Register();
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            Support.Log($"hotkey {wParam.ToInt32()} pressed");
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        source.RemoveHook(Hook);
        source.Dispose();
    }
}
