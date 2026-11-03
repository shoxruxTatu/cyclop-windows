using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Cyclop.Interop;
using Cyclop.UI;

namespace Cyclop.Services;

/// <summary>
/// Swallows every key and click so the keyboard and the screen can be wiped.
///
/// On Windows the mechanism is a pair of low-level hooks, which need no
/// permission — the Mac's Accessibility prompt has no counterpart here. What
/// they cannot stop is the secure attention sequence: Ctrl+Alt+Del and Win+L
/// belong to the system and always get through, which is what they are for.
///
/// Hold Esc or − for two seconds to unlock; it lifts by itself after a minute
/// regardless, and that way out does not depend on anything working.
/// </summary>
internal sealed class KeyboardLock : Observable
{
    public static readonly TimeSpan AutoRelease = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HoldToRelease = TimeSpan.FromSeconds(2);
    static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    readonly Dispatcher dispatcher;
    readonly Native.HookProc keyboardProc, mouseProc;
    IntPtr keyboardHook, mouseHook;
    DispatcherTimer? ticker;
    DateTime lockedAt;
    DateTime? holdStartedAt;
    int heldKey;
    int? drainKey;
    DateTime drainStartedAt;
    readonly List<Window> overlays = new();

    public bool IsLocked { get; private set; }
    public double HoldProgress { get; private set; }
    public int SecondsLeft { get; private set; } = (int)AutoRelease.TotalSeconds;
    public string? Failure { get; private set; }

    public KeyboardLock(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        // Held in fields: a delegate handed to native code and then collected
        // is the classic way for a hook to crash the process minutes later.
        keyboardProc = KeyboardCallback;
        mouseProc = MouseCallback;
    }

    public void Lock()
    {
        if (IsLocked) return;
        if (drainKey is not null) FinishDrain();
        var module = Native.GetModuleHandle(null);
        keyboardHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyboardProc, module, 0);
        mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
        if (keyboardHook == IntPtr.Zero || mouseHook == IntPtr.Zero)
        {
            Release();
            Failure = L10n.T("Could not take over input");
            Notify();
            return;
        }
        Failure = null;
        lockedAt = DateTime.Now;
        holdStartedAt = null;
        HoldProgress = 0;
        SecondsLeft = (int)AutoRelease.TotalSeconds;
        IsLocked = true;
        ShowOverlays();
        ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Normal, (_, _) => Tick(), dispatcher);
        ticker.Start();
        Notify();
    }

    public void Unlock()
    {
        if (!IsLocked && drainKey is null) return;
        drainKey = null;
        IsLocked = false;
        Release();
        Notify();
    }

    void Release()
    {
        ticker?.Stop();
        ticker = null;
        if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
        keyboardHook = mouseHook = IntPtr.Zero;
        holdStartedAt = null;
        HoldProgress = 0;
        HideOverlays();
    }

    /// <summary>The hold is satisfied: the cover comes off now, while the hook
    /// keeps swallowing the held key until it comes up — otherwise the tail of
    /// its auto-repeat would type into whatever is in front.</summary>
    void BeginDrain(int key)
    {
        drainKey = key;
        drainStartedAt = DateTime.Now;
        holdStartedAt = null;
        HoldProgress = 0;
        IsLocked = false;
        HideOverlays();
        if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
        Notify();
    }

    void FinishDrain()
    {
        if (drainKey is null && keyboardHook == IntPtr.Zero) return;
        drainKey = null;
        Release();
    }

    static bool IsReleaseKey(int vk) => vk is Native.VK_ESCAPE or Native.VK_OEM_MINUS or Native.VK_SUBTRACT;

    /// <summary>Kept short: Windows quietly removes a low-level hook whose
    /// callback takes too long.</summary>
    IntPtr KeyboardCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return Native.CallNextHookEx(keyboardHook, code, wParam, lParam);
        var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
        int vk = (int)info.vkCode;
        int msg = wParam.ToInt32();
        bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
        bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;

        if (drainKey is { } draining)
        {
            if (up && vk == draining) dispatcher.BeginInvoke(FinishDrain);
            return new IntPtr(1);
        }

        if (down && IsReleaseKey(vk))
        {
            // Auto-repeat sends keydown over and over; only the first counts.
            if (holdStartedAt is null) { holdStartedAt = DateTime.Now; heldKey = vk; }
        }
        else if ((up && IsReleaseKey(vk)) || down)
        {
            // Release key let go, or some other key caught by the cloth.
            holdStartedAt = null;
            HoldProgress = 0;
        }
        return new IntPtr(1);
    }

    IntPtr MouseCallback(int code, IntPtr wParam, IntPtr lParam) =>
        code < 0 ? Native.CallNextHookEx(mouseHook, code, wParam, lParam) : new IntPtr(1);

    void Tick()
    {
        var now = DateTime.Now;
        if (drainKey is not null)
        {
            if (now - drainStartedAt >= DrainTimeout) FinishDrain();
            return;
        }
        if (!IsLocked) return;
        if (holdStartedAt is { } started)
        {
            var held = now - started;
            HoldProgress = Math.Min(1, held / HoldToRelease);
            if (held >= HoldToRelease) { BeginDrain(heldKey); return; }
        }
        var elapsed = now - lockedAt;
        SecondsLeft = Math.Max(0, (int)Math.Ceiling((AutoRelease - elapsed).TotalSeconds));
        if (elapsed >= AutoRelease) { Unlock(); return; }
        UpdateOverlays();
    }

    // MARK: - Cover

    readonly List<(TextBlock Countdown, Border Progress)> overlayParts = new();

    /// <summary>Black on every display: dust and smudges show best on black,
    /// and a dark screen says "nothing here will respond" better than words.</summary>
    void ShowOverlays()
    {
        HideOverlays();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var title = Ui.Label(L10n.T("Keyboard and trackpad are locked"), 15, Theme.Secondary, FontWeights.Medium);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            var hint = Ui.Label(L10n.T("Hold Esc or − for %d s", (int)HoldToRelease.TotalSeconds), 12, Theme.Tertiary);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, 6, 0, 0);
            var countdown = Ui.Label("", 11, Theme.Tertiary);
            countdown.HorizontalAlignment = HorizontalAlignment.Center;
            countdown.Margin = new Thickness(0, 4, 0, 0);
            var progress = new Border { Height = 3, Background = Theme.White, HorizontalAlignment = HorizontalAlignment.Left, Width = 0, CornerRadius = new CornerRadius(1.5) };
            var track = new Border { Width = 160, Height = 3, Background = Theme.Surface, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(0, 14, 0, 0), Child = progress };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(Ui.Icon(Glyph.Keyboard, 30, Theme.Tertiary));
            title.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(title);
            stack.Children.Add(hint);
            stack.Children.Add(countdown);
            stack.Children.Add(track);

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                Background = Brushes.Black,
                Content = stack,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            var handle = new WindowInteropHelper(window).EnsureHandle();
            Native.AddExStyle(handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            var b = screen.Bounds;
            window.Show();
            Native.SetWindowPos(handle, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            overlays.Add(window);
            overlayParts.Add((countdown, progress));
        }
        UpdateOverlays();
    }

    void UpdateOverlays()
    {
        foreach (var (countdown, progress) in overlayParts)
        {
            countdown.Text = L10n.T("Unlocks by itself in %d s", SecondsLeft);
            progress.Width = 160 * HoldProgress;
        }
    }

    void HideOverlays()
    {
        foreach (var w in overlays) w.Close();
        overlays.Clear();
        overlayParts.Clear();
    }
}
