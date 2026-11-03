using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cyclop.App;
using Cyclop.Services;
using Cyclop.UI;

namespace Cyclop.Panes;

/// <summary>
/// Configuration, read rarely, laid out as a tab like any other. Rebuilt on
/// every visit: Task Manager can flip launch-at-login from outside, the
/// screenshots folder can fill or empty, and config.json is edited by hand.
/// </summary>
internal sealed class SettingsPane : Pane
{
    readonly StackPanel sections;
    readonly Func<bool> trayVisible;
    readonly Action<bool> setTrayVisible;
    readonly Action quit;
    Field? feedUrl, feedName;
    HotkeyRecorder? recording;
    Action? updateListener;

    public SettingsPane(AppModel model, IPanelHost host, Func<bool> trayVisible, Action<bool> setTrayVisible, Action quit)
        : base(model, host)
    {
        this.trayVisible = trayVisible;
        this.setTrayVisible = setTrayVisible;
        this.quit = quit;
        sections = new StackPanel { Margin = new Thickness(0, 2, 4, 0) };
        Children.Add(Ui.Scroller(sections));
    }

    protected override void OnShown() => Rebuild();

    protected override void OnHidden() => recording?.Cancel();

    public override bool OnEscape()
    {
        if (recording is null) return false;
        recording.Cancel();
        return true;
    }

    UIElement HotkeyRow(string glyph, string title, int id, Func<string> get, Action<string> set)
    {
        var icon = Ui.Icon(glyph, 11, Theme.Secondary);
        icon.Width = 16;
        var label = Ui.Label(title, 11.5, Theme.White, FontWeights.Medium);
        label.Margin = new Thickness(8, 0, 8, 0);
        var recorder = new HotkeyRecorder(get, value =>
        {
            set(value);
            Model.Hotkeys?.Resume();
        }, Model.Hotkeys?.Refused.Contains(id) == true);
        recorder.Started += () =>
        {
            recording?.Cancel();
            recording = recorder;
            Model.Hotkeys?.Suspend();
            Host.ClaimKeyboard();
            recorder.Focus();
        };
        recorder.Ended += () =>
        {
            if (recording == recorder) recording = null;
            Model.Hotkeys?.Resume();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (IsShown) Rebuild(); });
        };
        var line = new DockPanel { Height = 28, Margin = new Thickness(8, 0, 8, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(recorder, Dock.Right);
        line.Children.Add(icon);
        line.Children.Add(recorder);
        line.Children.Add(label);
        return line;
    }

    void Rebuild()
    {
        var config = Model.Config;
        sections.Children.Clear();

        Section(L10n.T("General"),
            ToggleRow(Glyph.Power, L10n.T("Launch at Login"), Autostart.IsEnabled, on => Autostart.IsEnabled = on),
            ToggleRow(Glyph.Eye, L10n.T("Show Tray Icon"), trayVisible(), on => { config.ShowTrayIcon = on; setTrayVisible(on); }),
            DelayRow());

        // The rail is for what gets a glance; whoever never uses a tab can take
        // it off, and its background work stops with it.
        Section(L10n.T("Show in Panel"), Tabs.LeftRail.Concat(Tabs.RightRail).Where(t => t.CanHide())
            .Select(t => ToggleRow(Glyph.For(t), t.Title(), Model.IsVisible(t), on => Model.SetVisible(t, on))).ToArray());

        Section(L10n.T("Displays"),
            ToggleRow(Glyph.Monitor, L10n.T("Show on All Displays"), config.ShowOnAllDisplays, on => config.ShowOnAllDisplays = on));

        var usage = ScreenshotVault.Usage();
        var clearTitle = usage.Files > 0
            ? L10n.T("Clear Screenshots Folder (%@)", Size(usage.Bytes))
            : L10n.T("Clear Screenshots Folder");
        Section(L10n.T("Screenshots"),
            HotkeyRow(Glyph.Crop, L10n.T("Screenshot of an Area"), HotkeyService.AreaId, () => config.HotkeyArea, v => config.HotkeyArea = v),
            HotkeyRow(Glyph.Monitor, L10n.T("Screenshot of the Screen"), HotkeyService.ScreenId, () => config.HotkeyScreen, v => config.HotkeyScreen = v),
            ToggleRow(Glyph.Photo, L10n.T("Save Clipboard Screenshots"), config.SaveClipboardImages, on => config.SaveClipboardImages = on),
            ToggleRow(Glyph.Eye, L10n.T("Watch Screenshots Folder"), Model.ScreenshotFolder.IsEnabled, WatchFolder,
                Model.ScreenshotFolder.IsEnabled ? config.ScreenshotFolder : null),
            ActionRow(Glyph.Folder, L10n.T("Show Screenshots Folder"), ScreenshotVault.Reveal),
            ActionRow(Glyph.Delete, clearTitle, () =>
            {
                ScreenshotVault.Clear();
                Model.Shelf.RefreshFromDisk();
                Rebuild();
            }, enabled: usage.Files > 0));

        Section(L10n.T("Calendars"), CalendarRows());

        Section(L10n.T("Files"),
            ActionRow(Glyph.Document, L10n.T("Show Snippets File"), () => { Model.Snippets.EnsureFile(); Reveal(Model.Snippets.FilePath); }),
            ActionRow(Glyph.Document, L10n.T("Show Teleprompter Script"), () => { Model.Teleprompter.EnsureFile(); Reveal(Model.Teleprompter.FilePath); }),
            config.FileBroken
                ? Notice(L10n.T("config.json is broken — click to open; nothing is overwritten"), () => Reveal(config.FilePath))
                : ActionRow(Glyph.Settings, L10n.T("Show Config File"), () => { config.EnsureFile(); Reveal(config.FilePath); }));

        Section(L10n.T("Updates"), UpdateRows());

        Section(L10n.T("About"),
            LinkRow(Glyph.Person, L10n.T("Windows version"), Author.Instagram, row => Open(Author.InstagramUrl)),
            LinkRow(Glyph.Heart, L10n.T("Support the author"), $"{Author.CardBank}  {Author.CardNumber}", row =>
            {
                // Digits only: what a banking app's transfer field accepts.
                Ui.Copy(Author.CardNumber.Replace(" ", ""));
                row.Text = L10n.T("Card number copied");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                timer.Tick += (_, _) => { timer.Stop(); row.Text = $"{Author.CardBank}  {Author.CardNumber}"; };
                timer.Start();
            }),
            LinkRow(Glyph.Globe, L10n.T("Based on Cyclop for macOS"), Author.OriginalAuthor, row => Open(Author.OriginalUrl)));

        Section("Cyclop", ActionRow(Glyph.Power, L10n.T("Quit Cyclop"), quit));
    }

    static string Size(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value.ToString(unit == 0 ? "0" : "0.#", L10n.Culture)} {L10n.T(units[unit])}";
    }

    static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    /// <summary>Turning on goes through the folder picker first; the switch
    /// follows what actually happened once it closes.</summary>
    void WatchFolder(bool on)
    {
        if (!on)
        {
            Model.ScreenshotFolder.Disable();
            Rebuild();
            return;
        }
        Ui.OpenPopups++;
        try
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = L10n.T("Cyclop will watch this folder for new screenshots and add them to the shelf."),
                UseDescriptionForTitle = true,
                SelectedPath = System.IO.Directory.Exists(ScreenshotFolderWatcher.DefaultFolder)
                    ? ScreenshotFolderWatcher.DefaultFolder
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Model.ScreenshotFolder.Enable(dialog.SelectedPath);
        }
        finally { Ui.OpenPopups--; }
        Rebuild();
    }

    UIElement[] CalendarRows()
    {
        var config = Model.Config;
        var rows = config.Calendars.Select((feed, index) =>
        {
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = Theme.FeedColors[index % Theme.FeedColors.Length], Margin = new Thickness(4, 0, 9, 0) };
            var name = Ui.Label(string.IsNullOrWhiteSpace(feed.Name) ? HostOf(feed.Url) : feed.Name, 11.5, Theme.White, FontWeights.Medium);
            var failure = Model.Calendar.Failures.TryGetValue(feed.Url, out var f) ? f : null;
            var url = Ui.Label(failure ?? HostOf(feed.Url), 10, failure is null ? Theme.Tertiary : Theme.Warning);
            url.Margin = new Thickness(8, 0, 0, 0);
            var toggle = new ToggleSwitch(feed.Enabled) { VerticalAlignment = VerticalAlignment.Center };
            toggle.Toggled += on => config.SetCalendarEnabled(feed, on);
            var remove = Ui.IconButton(Glyph.Close, () => { config.RemoveCalendar(feed); Rebuild(); }, 22, 9, tip: L10n.T("Delete"));
            remove.Margin = new Thickness(6, 0, 0, 0);
            var line = new DockPanel { Height = 26, Margin = new Thickness(8, 0, 8, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(remove, Dock.Right);
            DockPanel.SetDock(toggle, Dock.Right);
            line.Children.Add(dot);
            line.Children.Add(remove);
            line.Children.Add(toggle);
            line.Children.Add(name);
            line.Children.Add(url);
            return (UIElement)line;
        }).ToList();

        feedName = new Field(L10n.T("Name"));
        feedName.Width = 110;
        feedUrl = new Field(L10n.T("iCal link (https://… .ics)"));
        feedUrl.Margin = new Thickness(6, 0, 6, 0);
        var add = Ui.PillButton(L10n.T("Add"), AddFeed, Glyph.Add);
        foreach (var field in new[] { feedName, feedUrl })
        {
            field.Box.PreviewMouseDown += (_, _) => Host.ClaimKeyboard();
            field.Box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { AddFeed(); e.Handled = true; } };
        }
        var addRow = new DockPanel { Margin = new Thickness(6, 4, 6, 4) };
        DockPanel.SetDock(feedName, Dock.Left);
        DockPanel.SetDock(add, Dock.Right);
        addRow.Children.Add(feedName);
        addRow.Children.Add(add);
        addRow.Children.Add(feedUrl);
        rows.Add(addRow);

        var hint = Ui.Label(L10n.T("Google: calendar settings → Secret address in iCal format. Outlook: Settings → Shared calendars → Publish → ICS."), 9.5, Theme.Tertiary);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(10, 0, 10, 4);
        rows.Add(hint);
        return rows.ToArray();
    }

    static string HostOf(string url) =>
        Uri.TryCreate(url.Replace("webcal://", "https://"), UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : url;

    void AddFeed()
    {
        var url = feedUrl?.Text.Trim() ?? "";
        if (url.Length == 0) return;
        var name = feedName?.Text.Trim() ?? "";
        if (name.Length == 0) name = HostOf(url);
        Model.Config.AddCalendar(name, url);
        Model.Calendar.Refresh(force: true);
        Rebuild();
    }

    // MARK: - Rows

    void Section(string title, params UIElement[] rows)
    {
        var header = Ui.Caps(title);
        header.Margin = new Thickness(8, 0, 0, 3);
        var stack = new StackPanel();
        foreach (var row in rows) stack.Children.Add(row);
        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        block.Children.Add(header);
        block.Children.Add(Ui.Card(stack));
        sections.Children.Add(block);
    }

    static UIElement ToggleRow(string glyph, string title, bool on, Action<bool> changed, string? detail = null)
    {
        var icon = Ui.Icon(glyph, 11, Theme.Secondary);
        icon.Width = 16;
        var label = Ui.Label(title, 11.5, Theme.White, FontWeights.Medium);
        label.Margin = new Thickness(8, 0, 8, 0);
        var toggle = new ToggleSwitch(on) { VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += changed;
        var line = new DockPanel { Height = 26, Margin = new Thickness(8, 0, 8, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(toggle, Dock.Right);
        line.Children.Add(icon);
        line.Children.Add(toggle);
        line.Children.Add(label);
        if (detail is not null)
        {
            var d = Ui.Label(detail, 10, Theme.Tertiary);
            d.HorizontalAlignment = HorizontalAlignment.Right;
            d.Margin = new Thickness(0, 0, 8, 0);
            line.Children.Add(d);
        }
        return line;
    }

    UIElement[] UpdateRows()
    {
        var config = Model.Config;
        var updates = Model.Updates;
        var version = Ui.Label(L10n.T("Version %@", UpdateService.CurrentVersion), 11.5, Theme.White, FontWeights.Medium);
        var status = Ui.Label("", 10.5, Theme.Secondary);
        status.Margin = new Thickness(10, 0, 0, 0);
        var line = new DockPanel { Height = 26, Margin = new Thickness(8, 0, 8, 0) };
        var icon = Ui.Icon(Glyph.Refresh, 11, Theme.Secondary);
        icon.Width = 16;
        version.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(version, Dock.Left);
        line.Children.Add(icon);
        line.Children.Add(version);
        Clickable? action = null;
        if (updates is not null && UpdateService.IsEnabled)
        {
            action = Ui.PillButton("", () =>
            {
                if (updates.Available is not null) updates.InstallNow(); else updates.CheckNow();
            });
            action.Padding = new Thickness(10, 2, 10, 2);
            DockPanel.SetDock(action, Dock.Right);
            line.Children.Add(action);
        }
        line.Children.Add(status);

        void Refresh()
        {
            if (updates is null || !UpdateService.IsEnabled)
            {
                status.Text = L10n.T("Updates are off in this build");
                return;
            }
            status.Text = updates.Status ?? "";
            if (action?.Child is StackPanel row && row.Children[^1] is TextBlock label)
                label.Text = updates.Available is { } a ? L10n.T("Install %@", a.Version) : L10n.T("Check Now");
            if (action is not null) action.IsEnabled = !updates.Busy;
        }
        // One listener at a time: every visit rebuilds these rows.
        if (updates is not null)
        {
            if (updateListener is not null) updates.Changed -= updateListener;
            updateListener = () => { if (IsShown) Refresh(); };
            updates.Changed += updateListener;
        }
        Refresh();

        return new[]
        {
            line,
            ToggleRow(Glyph.Refresh, L10n.T("Install Updates Automatically"), config.AutoUpdate, on => config.AutoUpdate = on),
            ToggleRow(Glyph.Person, L10n.T("Anonymous Usage Count"), config.AnonymousStats, on => config.AnonymousStats = on),
            Hint(L10n.T("Once in a while Cyclop asks the update server for the newest version. With the count on, it adds a random install number, so the author can see how many people use Cyclop. No name, files or contents are sent.")),
        };
    }

    static UIElement Hint(string text)
    {
        var hint = Ui.Label(text, 9.5, Theme.Tertiary);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(10, 2, 10, 4);
        return hint;
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    /// <summary>A title and, on the right, the value it is about; the whole
    /// row is the button. The action gets the value label, to say what it did.</summary>
    static UIElement LinkRow(string glyph, string title, string detail, Action<TextBlock> action)
    {
        var icon = Ui.Icon(glyph, 11, Theme.Secondary);
        icon.Width = 16;
        var label = Ui.Label(title, 11.5, Theme.White, FontWeights.Medium);
        label.Margin = new Thickness(8, 0, 8, 0);
        var value = Ui.Label(detail, 11, Theme.Secondary);
        var line = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        line.Children.Add(icon);
        line.Children.Add(value);
        line.Children.Add(label);
        var row = new Clickable
        {
            Height = 26,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            Hover = Theme.Surface,
            Child = line,
        };
        row.Click += () => action(value);
        return row;
    }

    static UIElement ActionRow(string glyph, string title, Action action, bool enabled = true)
    {
        var icon = Ui.Icon(glyph, 11, Theme.Secondary);
        icon.Width = 16;
        var label = Ui.Label(title, 11.5, Theme.White, FontWeights.Medium);
        label.Margin = new Thickness(8, 0, 0, 0);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(icon);
        line.Children.Add(label);
        var row = new Clickable
        {
            Height = 26,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(6),
            Hover = Theme.Surface,
            Child = line,
            IsEnabled = enabled,
        };
        row.Click += action;
        return row;
    }

    static UIElement Notice(string text, Action action)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(Ui.Icon(Glyph.Warning, 10, Theme.Warning));
        var label = Ui.Label(text, 10.5, Theme.Secondary);
        label.Margin = new Thickness(8, 0, 0, 0);
        line.Children.Add(label);
        var row = new Clickable { Height = 26, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(6), Hover = Theme.Surface, Child = line };
        row.Click += action;
        return row;
    }

    /// <summary>The wait before the panel opens. From 50 ms, for whoever never
    /// overshoots, to well past any overshoot.</summary>
    UIElement DelayRow()
    {
        var config = Model.Config;
        var icon = Ui.Icon(Glyph.Timer, 11, Theme.Secondary);
        icon.Width = 16;
        var label = Ui.Label(L10n.T("Open Delay"), 11.5, Theme.White, FontWeights.Medium);
        label.Margin = new Thickness(8, 0, 8, 0);
        var value = Ui.Label(L10n.T("%d ms", (int)Math.Round(config.OpenDelay * 1000)), 11, Theme.Secondary);
        value.Width = 50;
        value.TextAlignment = TextAlignment.Right;
        var slider = new SliderBar(ConfigStore.OpenDelayRange.Min, ConfigStore.OpenDelayRange.Max, config.OpenDelay, 0.05) { Width = 110, VerticalAlignment = VerticalAlignment.Center };
        slider.ShowsThumb = true;
        slider.Changing += v => value.Text = L10n.T("%d ms", (int)Math.Round(v * 1000));
        slider.Committed += v => config.OpenDelay = v;
        var line = new DockPanel { Height = 26, Margin = new Thickness(8, 0, 8, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(value, Dock.Right);
        DockPanel.SetDock(slider, Dock.Right);
        line.Children.Add(icon);
        line.Children.Add(value);
        line.Children.Add(slider);
        line.Children.Add(label);
        return line;
    }
}

/// <summary>
/// A shortcut field: click it, press the combination. Esc leaves it as it was,
/// Backspace or Delete turns the shortcut off. A combination Windows refused
/// — another app owns it — is shown in yellow.
/// </summary>
internal sealed class HotkeyRecorder : Border
{
    readonly Func<string> get;
    readonly Action<string> save;
    readonly TextBlock text;
    readonly bool refused;
    bool active;

    public event Action? Started;
    public event Action? Ended;

    public HotkeyRecorder(Func<string> get, Action<string> save, bool refused)
    {
        this.get = get;
        this.save = save;
        this.refused = refused;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Hand;
        MinWidth = 120;
        Height = 22;
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(8, 0, 8, 0);
        VerticalAlignment = VerticalAlignment.Center;
        text = Ui.Label("", 11, Theme.White, FontWeights.Medium);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        Child = text;
        ToolTip = L10n.T("Click and press a new shortcut. Backspace turns it off.");
        Show();
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (active) return;
            active = true;
            Show();
            Started?.Invoke();
        };
        PreviewKeyDown += OnKey;
        LostKeyboardFocus += (_, _) => Cancel();
    }

    void Show()
    {
        var value = get();
        Background = active ? Theme.AccentSoft : Theme.SurfaceHover;
        text.Foreground = !active && refused ? Theme.Warning : Theme.White;
        text.Text = active ? L10n.T("Press a shortcut…")
            : string.IsNullOrWhiteSpace(value) ? L10n.T("Off")
            : refused ? value + "  · " + L10n.T("taken") : value;
    }

    public void Cancel()
    {
        if (!active) return;
        active = false;
        Show();
        Ended?.Invoke();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (!active) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.Escape) { Cancel(); return; }
        if (key is Key.Back or Key.Delete)
        {
            Commit("");
            return;
        }
        if (Hotkey.IsModifierKey(key))
        {
            // Modifiers alone: show them, wait for the key.
            text.Text = Hotkey.ModifiersText(Keyboard.Modifiers) + "+…";
            return;
        }
        var hotkey = new Hotkey(Keyboard.Modifiers, key);
        if (!hotkey.IsUsable)
        {
            text.Text = L10n.T("Add Ctrl, Alt or Win");
            return;
        }
        Commit(hotkey.ToString());
    }

    void Commit(string value)
    {
        active = false;
        save(value);
        Show();
        Ended?.Invoke();
    }
}
