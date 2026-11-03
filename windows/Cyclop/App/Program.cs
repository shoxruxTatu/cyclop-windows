using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cyclop.Panel;
using Cyclop.Services;
using Cyclop.UI;

namespace Cyclop;

internal static class Program
{
    const string InstanceName = @"Local\Cyclop.Windows.Instance";
    const string ReopenName = @"Local\Cyclop.Windows.Reopen";
    const string QuitName = @"Local\Cyclop.Windows.Quit";

    [STAThread]
    static void Main(string[] args)
    {
        // One Cyclop at a time. Launching the same build again is the way back
        // once the tray icon has been switched off: the running copy brings the
        // icon back and opens the panel, and the new one quits. Launching a
        // different build — a new version — replaces the running one instead;
        // otherwise the old copy would go on running and the new exe would
        // seem to do nothing.
        var identity = BuildIdentity();
        using var mutex = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first)
        {
            if (RunningIdentity() == identity)
            {
                try { EventWaitHandle.OpenExisting(ReopenName).Set(); } catch { }
                return;
            }
            if (!TakeOver(mutex)) return;
        }
        WriteIdentity(identity);
        Support.Log($"started {identity}");
        using var reopen = new EventWaitHandle(false, EventResetMode.AutoReset, ReopenName);
        using var quitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, QuitName);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) =>
        {
            // A broken pane must not take the clipboard history and the shelf
            // down with it.
            Support.Log("unhandled: " + e.Exception);
            e.Handled = true;
        };
        InstallStyles(app);

        App.AppModel? model = null;
        PanelController? controller = null;
        App.TrayIcon? tray = null;

        void Quit()
        {
            model?.Updates?.Stop();
            model?.Hotkeys?.Dispose();
            controller?.Stop();
            model?.Stop();
            tray?.Dispose();
            app.Shutdown();
        }

        app.Startup += (_, _) =>
        {
            model = new App.AppModel(app.Dispatcher);
            tray = new App.TrayIcon(model);
            controller = new PanelController(model, () => tray.Visible, on => tray.Visible = on, Quit);
            tray.TogglePanel += controller.Toggle;
            tray.Quit += Quit;

            var screenshots = new ScreenshotTool(model, controller);
            var hotkeys = new HotkeyService(model.Config)
            {
                // A short wait lets the keys of the shortcut come up before
                // the overlay asks for the keyboard.
                Area = () => app.Dispatcher.BeginInvoke(DispatcherPriority.Background, screenshots.StartArea),
                Screen = () => app.Dispatcher.BeginInvoke(DispatcherPriority.Background, screenshots.TakeScreen),
            };
            model.Screenshots = screenshots;
            model.Hotkeys = hotkeys;
            tray.AreaShot += screenshots.StartArea;
            tray.ScreenShot += screenshots.TakeScreen;
            var updates = new UpdateService(model.Config, model.State,
                canRestart: () => !controller.IsOpen && !screenshots.IsActive && !model.KeyboardLock.IsLocked,
                quit: Quit);
            model.Updates = updates;
            model.Start();
            controller.Start();
            updates.Start();
            UpdateService.CleanUpAfterUpdate();
            int updated = Array.IndexOf(args, "--updated");
            if (updated >= 0)
                tray.Notify(L10n.T("Cyclop has been updated to %@.", UpdateService.CurrentVersion), warning: false);
            else if (UpdateService.IsEnabled && model.Config.AnonymousStats && !model.State.StatsNoticeShown)
            {
                // Said once, up front, rather than buried in a file.
                tray.Notify(L10n.T("Cyclop checks for updates and anonymously counts how many people use it. No personal data is sent. Turn it off in Settings → Updates."), warning: false);
                model.State.StatsNoticeShown = true;
                model.State.Save();
            }
            Autostart.RefreshPathIfEnabled();

            var waiter = new Thread(() =>
            {
                while (reopen.WaitOne())
                {
                    app.Dispatcher.BeginInvoke(() =>
                    {
                        model.Config.ShowTrayIcon = true;
                        tray.Visible = true;
                        controller.Toggle();
                    });
                }
            }) { IsBackground = true, Name = "Cyclop reopen" };
            waiter.Start();
            var quitter = new Thread(() =>
            {
                if (quitRequest.WaitOne()) app.Dispatcher.BeginInvoke(Quit);
            }) { IsBackground = true, Name = "Cyclop quit" };
            quitter.Start();

            // A shortcut another app already owns cannot be taken; say so once
            // rather than leave the keys silently doing nothing.
            if (hotkeys.Refused.Count > 0)
            {
                var taken = string.Join(", ", hotkeys.Refused.Select(id => id == HotkeyService.AreaId ? model.Config.HotkeyArea : model.Config.HotkeyScreen));
                tray.Notify(L10n.T("Shortcut %@ is taken by another app. Pick another in Settings → Screenshots.", taken));
            }
        };
        app.SessionEnding += (_, _) => Quit();
        app.Run();
    }

    /// <summary>Which build this is: the exe's path and the time it was
    /// written. Two copies of the same file agree; a rebuilt or moved one does not.</summary>
    static string BuildIdentity()
    {
        var path = Environment.ProcessPath ?? "";
        long stamp = 0;
        try { stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3);
        return $"{version} {path} {stamp}";
    }

    static string IdentityFile => Support.File("instance.txt");

    static string? RunningIdentity()
    {
        try { return System.IO.File.ReadAllText(IdentityFile).Trim(); } catch { return null; }
    }

    static void WriteIdentity(string identity)
    {
        try { System.IO.File.WriteAllText(IdentityFile, identity); } catch { }
    }

    /// <summary>Asks the running copy to quit and waits for it. A copy too old
    /// to listen for the request is closed the hard way: its notes are written
    /// within a second of typing, so nothing worth keeping is lost.</summary>
    static bool TakeOver(Mutex mutex)
    {
        bool asked = false;
        try { EventWaitHandle.OpenExisting(QuitName).Set(); asked = true; } catch { }
        if (asked && Acquire(mutex, TimeSpan.FromSeconds(6))) return true;
        var me = Environment.ProcessId;
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("Cyclop"))
        {
            try { if (process.Id != me) process.Kill(); } catch { }
        }
        return Acquire(mutex, TimeSpan.FromSeconds(6));
    }

    static bool Acquire(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>The few system controls the panel uses — menus, tooltips —
    /// in the panel's colours rather than the light defaults.</summary>
    static void InstallStyles(Application app)
    {
        var dark = Theme.Hex("#202020");
        var border = Theme.Hex("#3A3A3A");

        var menu = new Style(typeof(ContextMenu));
        menu.Setters.Add(new Setter(Control.BackgroundProperty, dark));
        menu.Setters.Add(new Setter(Control.ForegroundProperty, Theme.White));
        menu.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        menu.Setters.Add(new Setter(Control.FontFamilyProperty, Theme.Font));
        menu.Setters.Add(new Setter(Control.FontSizeProperty, 12.0));
        app.Resources.Add(typeof(ContextMenu), menu);

        var item = new Style(typeof(MenuItem));
        item.Setters.Add(new Setter(Control.ForegroundProperty, Theme.White));
        item.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        app.Resources.Add(typeof(MenuItem), item);

        var tip = new Style(typeof(ToolTip));
        tip.Setters.Add(new Setter(Control.BackgroundProperty, dark));
        tip.Setters.Add(new Setter(Control.ForegroundProperty, Theme.White));
        tip.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        tip.Setters.Add(new Setter(Control.FontFamilyProperty, Theme.Font));
        tip.Setters.Add(new Setter(Control.FontSizeProperty, 11.0));
        app.Resources.Add(typeof(ToolTip), tip);

        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(700));
    }
}
