using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Cyclop.App;
using Cyclop.Interop;
using Cyclop.UI;
using Microsoft.Win32;

namespace Cyclop.Panel;

/// <summary>
/// Decides when the panel is open, from where the pointer is.
///
/// The pointer is sampled on a timer rather than through mouse events — the
/// same choice as on the Mac, for the same reason: events depend on which
/// window happens to be under the cursor, and reading the position does not.
/// Fast near the top edge and while open, slow everywhere else, so at rest it
/// costs nothing measurable.
/// </summary>
internal sealed class PanelController
{
    readonly AppModel model;
    readonly PanelWindow panel;
    readonly Dispatcher dispatcher;
    readonly DispatcherTimer timer;
    readonly Dictionary<string, StripWindow> strips = new();
    readonly HashSet<string> fullscreen = new();
    List<Monitor> monitors = new();
    bool appliedAllDisplays;

    Monitor? openOn;
    Monitor? hoverOn;
    DateTime hoverSince;
    DateTime? leftAt;
    bool manual, enteredSinceManual, wasPressed;
    DateTime lastSweep;

    public PanelController(AppModel model, Func<bool> trayVisible, Action<bool> setTrayVisible, Action quit)
    {
        this.model = model;
        dispatcher = Dispatcher.CurrentDispatcher;
        panel = new PanelWindow(model, Close, trayVisible, setTrayVisible, quit);
        panel.EnsureHandle();
        timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Tick();
        appliedAllDisplays = model.Config.ShowOnAllDisplays;
        model.Config.Changed += () =>
        {
            if (model.Config.ShowOnAllDisplays == appliedAllDisplays) return;
            appliedAllDisplays = model.Config.ShowOnAllDisplays;
            Rebuild();
        };
        SystemEvents.DisplaySettingsChanged += (_, _) => dispatcher.BeginInvoke(Rebuild);
        // A sleeping display has no hover to watch; waking starts folded.
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Suspend) dispatcher.BeginInvoke(Close);
            if (e.Mode == PowerModes.Resume) dispatcher.BeginInvoke(Rebuild);
        };
        model.KeyboardLock.Changed += () => { if (model.KeyboardLock.IsLocked) Close(); };
    }

    public void Start()
    {
        Rebuild();
        timer.Start();
    }

    public void Stop()
    {
        timer.Stop();
        foreach (var strip in strips.Values) strip.Close();
        strips.Clear();
        panel.Close();
    }

    // MARK: - Displays

    void Rebuild()
    {
        if (openOn is not null) Close();
        monitors = Enumerate();
        if (!model.Config.ShowOnAllDisplays) monitors = monitors.Where(m => m.IsPrimary).Take(1).ToList();
        foreach (var gone in strips.Keys.Where(k => monitors.All(m => m.Device != k)).ToList())
        {
            strips[gone].Close();
            strips.Remove(gone);
        }
        foreach (var monitor in monitors)
        {
            if (!strips.TryGetValue(monitor.Device, out var strip))
            {
                strip = new StripWindow();
                strips[monitor.Device] = strip;
            }
            strip.PlaceOn(monitor);
        }
        Sweep(force: true);
    }

    internal static List<Monitor> Enumerate()
    {
        var list = new List<Monitor>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (!Native.GetMonitorInfo(handle, ref info)) return true;
            double scale = 1;
            if (Native.GetDpiForMonitor(handle, 0, out var dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96.0;
            list.Add(new Monitor(handle, info.szDevice, info.rcMonitor, scale, (info.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        // Mirrored displays report the same rect twice; one panel is enough.
        return list.GroupBy(m => (m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Bottom)).Select(g => g.First()).ToList();
    }

    /// <summary>
    /// Every half second: which displays have a full-screen app in front — a
    /// game, a video, a slideshow — and lose their strip and their hover zone
    /// until it leaves; and the strips put back on top of anything topmost
    /// that opened since.
    /// </summary>
    void Sweep(bool force = false)
    {
        var now = DateTime.Now;
        if (!force && now - lastSweep < TimeSpan.FromMilliseconds(500)) return;
        lastSweep = now;
        fullscreen.Clear();
        var foreground = Native.GetForegroundWindow();
        // A maximised window can cover the display too, with the taskbar set
        // to hide; only a window that is not maximised and still covers every
        // pixel is full screen.
        if (foreground != IntPtr.Zero && !IsShell(foreground) && !Native.IsZoomed(foreground) &&
            Native.GetWindowRect(foreground, out var r))
        {
            foreach (var m in monitors)
            {
                var b = m.Bounds;
                if (r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom) fullscreen.Add(m.Device);
            }
        }
        foreach (var m in monitors)
        {
            if (!strips.TryGetValue(m.Device, out var strip)) continue;
            bool show = !capturing && !fullscreen.Contains(m.Device) && openOn?.Device != m.Device;
            strip.SetShown(show);
        }
    }

    static readonly string[] ShellClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    static bool IsShell(IntPtr hwnd)
    {
        var name = new StringBuilder(64);
        Native.GetClassName(hwnd, name, name.Capacity);
        return ShellClasses.Contains(name.ToString()) || IsOurs(hwnd);
    }

    static bool IsOurs(IntPtr hwnd) =>
        Application.Current?.Windows.Cast<Window>().Any(w => new WindowInteropHelper(w).Handle == hwnd) ?? false;

    /// <summary>Where the pointer has to come to: the strip, with some slack on
    /// either side, down to just under it — the top edge is where a flung
    /// pointer stops on its own.</summary>
    static Native.RECT HoverZone(Monitor m)
    {
        int half = (int)Math.Round((Theme.StripWidth / 2 + 30) * m.Scale);
        return new Native.RECT
        {
            Left = m.CenterX - half,
            Right = m.CenterX + half,
            Top = m.Bounds.Top,
            Bottom = m.Bounds.Top + (int)Math.Max(3, Math.Round((Theme.StripHeight + 2) * m.Scale)),
        };
    }

    // MARK: - Pointer

    void Tick()
    {
        var now = DateTime.Now;
        var p = Native.Cursor();
        bool pressed = Native.IsDown(Native.VK_LBUTTON) || Native.IsDown(Native.VK_RBUTTON);
        bool pressEdge = pressed && !wasPressed;
        wasPressed = pressed;
        Sweep();

        if (openOn is not null)
        {
            timer.Interval = TimeSpan.FromMilliseconds(16);
            if (!panel.IsOpen) { openOn = null; return; }
            bool inside = panel.HitRect().Contains(p);
            if (inside)
            {
                leftAt = null;
                if (manual) enteredSinceManual = true;
            }

            // The one place the pointer does not decide: a running script is
            // read hands-off. It ends with Esc, the script running out, or a
            // click anywhere outside.
            if (model.HoldsOpen)
            {
                // Esc is read from the hardware: a reading panel does not
                // hold the keyboard.
                if (Native.IsDown(Native.VK_ESCAPE)) { model.Teleprompter.Pause(); return; }
                if (pressEdge && !inside)
                {
                    model.Teleprompter.Suspend();
                    Close();
                }
                return;
            }
            if (Ui.OpenPopups > 0 || panel.IsDragOver || inside) return;

            // Opened from the tray: the pointer starts far away, so leaving
            // only counts once it has been on the panel. Until then a click
            // elsewhere closes it.
            if (manual && !enteredSinceManual)
            {
                if (pressEdge) Close();
                return;
            }
            leftAt ??= now;
            var grace = model.IsTyping ? 0.6 : 0.2;
            if ((now - leftAt.Value).TotalSeconds >= grace) Close();
            return;
        }

        var zone = capturing ? null : monitors.FirstOrDefault(m => !fullscreen.Contains(m.Device) && HoverZone(m).Contains(p));
        if (zone is null)
        {
            hoverOn = null;
            var near = monitors.Any(m => p.X >= m.Bounds.Left && p.X < m.Bounds.Right && p.Y - m.Bounds.Top < 60 * m.Scale && p.Y >= m.Bounds.Top);
            timer.Interval = TimeSpan.FromMilliseconds(near ? 16 : 100);
            return;
        }
        timer.Interval = TimeSpan.FromMilliseconds(16);
        if (hoverOn?.Device != zone.Device)
        {
            hoverOn = zone;
            hoverSince = now;
            return;
        }
        // Staying put is what asks for the panel; a pointer that overshot on
        // its way to a browser tab turns back well within the delay.
        if ((now - hoverSince).TotalSeconds >= model.Config.OpenDelay) Open(zone, fromTray: false);
    }

    void Open(Monitor monitor, bool fromTray)
    {
        if (model.KeyboardLock.IsLocked) return;
        openOn = monitor;
        hoverOn = null;
        leftAt = null;
        manual = fromTray;
        enteredSinceManual = false;
        if (strips.TryGetValue(monitor.Device, out var strip)) strip.SetShown(false);
        panel.Open(monitor);
        model.SetPanelActive(true);
        timer.Interval = TimeSpan.FromMilliseconds(16);
    }

    public void Close()
    {
        if (openOn is null && !panel.IsOpen) return;
        var monitor = openOn;
        openOn = null;
        hoverOn = null;
        if (model.Teleprompter.IsRunning) model.Teleprompter.Suspend();
        model.SetPanelActive(false);
        // Back to covered: a row uncovered once must not still be uncovered
        // the next time, which is exactly when the camera may be on.
        model.Privacy.CoverEverything();
        panel.Collapse(() =>
        {
            if (monitor is not null && strips.TryGetValue(monitor.Device, out var strip) && !fullscreen.Contains(monitor.Device))
                strip.SetShown(true);
        });
    }

    bool capturing;

    public bool IsOpen => openOn is not null;

    /// <summary>Out of the picture before a screenshot: the panel folds and
    /// the strips go, then a moment for the screen to repaint without them.</summary>
    public async System.Threading.Tasks.Task HideForCaptureAsync()
    {
        capturing = true;
        bool wasOpen = panel.IsOpen;
        Close();
        foreach (var strip in strips.Values) strip.SetShown(false);
        await System.Threading.Tasks.Task.Delay(wasOpen ? 450 : 80);
    }

    public void RestoreAfterCapture()
    {
        capturing = false;
        Sweep(force: true);
    }

    /// <summary>From the tray icon: open on the display the pointer is on.</summary>
    public void Toggle()
    {
        if (openOn is not null) { Close(); return; }
        var p = Native.Cursor();
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(p)) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (monitor is not null) Open(monitor, fromTray: true);
    }
}

/// <summary>
/// The folded panel: a short black strip at the top centre of a display.
/// Purely a picture — click-through, never focused — the hover zone around it
/// is watched by the controller.
/// </summary>
internal sealed class StripWindow : Window
{
    readonly IntPtr hwnd;
    bool shown;

    public StripWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = Theme.StripWidth;
        Height = Theme.StripHeight;
        Content = new Border
        {
            Background = Theme.Black,
            CornerRadius = new CornerRadius(0, 0, Theme.StripRadius, Theme.StripRadius),
        };
        hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED);
    }

    Monitor? monitor;

    public void PlaceOn(Monitor m)
    {
        monitor = m;
        if (shown) Position();
    }

    void Position()
    {
        if (monitor is null) return;
        int w = (int)Math.Round(Theme.StripWidth * monitor.Scale);
        int h = (int)Math.Round(Theme.StripHeight * monitor.Scale);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, monitor.CenterX - w / 2, monitor.Bounds.Top, w, h, Native.SWP_NOACTIVATE);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, monitor.CenterX - w / 2, monitor.Bounds.Top, w, h, Native.SWP_NOACTIVATE);
    }

    public void SetShown(bool show)
    {
        if (show)
        {
            if (!shown) { Position(); Show(); shown = true; Position(); }
            // Back on top of anything topmost opened since.
            else Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else if (shown)
        {
            Hide();
            shown = false;
        }
    }
}
