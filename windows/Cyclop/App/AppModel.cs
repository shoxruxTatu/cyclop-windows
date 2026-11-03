using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Cyclop.Calendar;
using Cyclop.Services;

namespace Cyclop.App;

/// <summary>
/// The shared state behind the panel — the tab, the stores, the services —
/// the counterpart of <c>NotchViewModel</c> on the Mac. The panel window is
/// one per app, so this has no notion of displays.
/// </summary>
internal sealed class AppModel
{
    public ConfigStore Config { get; }
    public AppState State { get; }
    public MediaService Media { get; }
    public ShelfStore Shelf { get; }
    public ClipboardHistory Clipboard { get; } = new();
    public ClipboardWatcher ClipboardWatcher { get; }
    public ScreenshotFolderWatcher ScreenshotFolder { get; }
    public CalendarStore Calendar { get; }
    public Translator Translator { get; } = new();
    public CurrencyStore Currencies { get; }
    public SnippetStore Snippets { get; } = new();
    public NoteStore Notes { get; } = new();
    public TeleprompterStore Teleprompter { get; }
    public PrivacyMode Privacy { get; }
    public KeyboardLock KeyboardLock { get; }
    /// <summary>Set once the panel exists: the tool folds the panel out of the
    /// picture before it captures.</summary>
    public Panel.ScreenshotTool? Screenshots { get; set; }
    public HotkeyService? Hotkeys { get; set; }
    public UpdateService? Updates { get; set; }

    readonly HashSet<Tab> hidden;
    Tab tab = Tab.Media;
    bool started;

    /// <summary>Old tab, new tab.</summary>
    public event Action<Tab, Tab>? TabChanged;
    public event Action? RailChanged;

    public bool IsPanelActive { get; private set; }
    /// <summary>Whether the panel holds the keyboard right now.</summary>
    public bool IsTyping { get; set; }

    public AppModel(Dispatcher dispatcher)
    {
        Config = ConfigStore.Shared;
        State = AppState.Shared;
        Media = new MediaService(dispatcher);
        Shelf = new ShelfStore(State, dispatcher);
        ClipboardWatcher = new ClipboardWatcher(Clipboard, dispatcher);
        ScreenshotFolder = new ScreenshotFolderWatcher(Config, dispatcher);
        Calendar = new CalendarStore(Config);
        Currencies = new CurrencyStore(State);
        Teleprompter = new TeleprompterStore(Config);
        Privacy = new PrivacyMode(Config);
        KeyboardLock = new KeyboardLock(dispatcher);
        hidden = Config.HiddenTabs.Select(Tabs.FromId).Where(t => t is { } v && v.CanHide()).Select(t => t!.Value).ToHashSet();
    }

    // MARK: - Rail

    public bool IsVisible(Tab t) => !hidden.Contains(t);
    public IEnumerable<Tab> LeftRail => Tabs.LeftRail.Where(IsVisible);
    public IEnumerable<Tab> RightRail => Tabs.RightRail.Where(IsVisible);
    Tab FirstVisible => LeftRail.Concat(RightRail).FirstOrDefault(Tab.Settings);

    /// <summary>Off means two things: the icon leaves the rail, and the tab's
    /// background work stops with it. A hidden tab costs nothing.</summary>
    public void SetVisible(Tab target, bool visible)
    {
        if (!target.CanHide() || IsVisible(target) == visible) return;
        if (visible)
        {
            hidden.Remove(target);
            if (started) StartBackground(target);
        }
        else
        {
            hidden.Add(target);
            StopBackground(target);
            if (tab == target) Tab = FirstVisible;
        }
        Config.HiddenTabs = hidden.Select(t => t.Id()).OrderBy(s => s).ToList();
        RailChanged?.Invoke();
    }

    void StartBackground(Tab target)
    {
        switch (target)
        {
            case Tab.Media:
                Media.Start();
                if (IsPanelActive) Media.SetActive(true);
                break;
            case Tab.Clipboard: ClipboardWatcher.Start(); break;
            case Tab.Calendar:
                Calendar.Start();
                if (IsPanelActive) Calendar.SetActive(true);
                break;
            case Tab.Shelf: ScreenshotFolder.ResumeIfEnabled(); break;
            case Tab.Currency: Currencies.Start(); break;
        }
    }

    void StopBackground(Tab target)
    {
        switch (target)
        {
            case Tab.Media: Media.Stop(); break;
            case Tab.Clipboard: ClipboardWatcher.Stop(); break;
            case Tab.Calendar: Calendar.Stop(); break;
            case Tab.Shelf: ScreenshotFolder.Stop(); break;
            case Tab.Currency: Currencies.Stop(); break;
            case Tab.Utilities: KeyboardLock.Unlock(); break;
        }
    }

    // MARK: - Tab

    public Tab Tab
    {
        get => tab;
        set
        {
            if (tab == value) return;
            var old = tab;
            tab = value;
            // The snippets file is edited from outside the app, so it is read
            // on the way in rather than held from launch.
            if (value == Tab.Snippets) Snippets.Reload();
            if (value == Tab.Shelf) Shelf.RefreshFromDisk();
            if (value == Tab.Currency) Currencies.RefreshIfNeeded();
            if (value == Tab.Calendar) Calendar.Refresh();
            // Leaving the notes sweeps out the blank ones.
            if (old == Tab.Notes) Notes.Leave();
            // Leaving the teleprompter stops the scroll, so the panel goes back
            // to obeying the pointer like everything else.
            if (old == Tab.Teleprompter) Teleprompter.Suspend();
            TabChanged?.Invoke(old, value);
        }
    }

    /// <summary>Whether the panel must stay open with no pointer on it: one
    /// tab, only while the script is actually moving.</summary>
    public bool HoldsOpen => tab == Tab.Teleprompter && Teleprompter.IsRunning;

    /// <summary>Whether a click into the panel should hand it the keyboard.
    /// The teleprompter does only while it is an editor.</summary>
    public bool ClickTakesKeyboard =>
        tab.NeedsKeyboard() || (tab == Tab.Teleprompter && Teleprompter.Script.Length == 0);

    public void SetPanelActive(bool active)
    {
        if (active == IsPanelActive) return;
        IsPanelActive = active;
        if (IsVisible(Tab.Media)) Media.SetActive(active);
        if (IsVisible(Tab.Calendar)) Calendar.SetActive(active);
    }

    // MARK: - Lifecycle

    public void Start()
    {
        Shelf.Load();
        Snippets.Reload();
        ClipboardWatcher.WantsImages = () => Config.SaveClipboardImages;
        ClipboardWatcher.OnImage = png =>
        {
            if (ScreenshotVault.Save(png) is { } path) ReceivedScreenshot(path);
        };
        ScreenshotFolder.OnImage = ReceivedScreenshot;
        started = true;
        foreach (var t in Enum.GetValues<Tab>().Where(IsVisible)) StartBackground(t);
        if (!IsVisible(tab)) Tab = FirstVisible;
    }

    public void Stop()
    {
        started = false;
        foreach (var t in Enum.GetValues<Tab>()) StopBackground(t);
        Notes.Flush();
        Teleprompter.Flush();
    }

    /// <summary>A screenshot that arrived on its own. It goes on the shelf
    /// either way, but the panel switches to it only when nobody is
    /// mid-sentence: the field would slide out from under the caret.</summary>
    void ReceivedScreenshot(string path)
    {
        Shelf.Add(new[] { path });
        if (IsTyping || !IsVisible(Tab.Shelf)) return;
        Tab = Tab.Shelf;
    }

    /// <summary>Files dropped on the panel by hand. Refused when the shelf is
    /// off: a drop that lands nowhere visible is worse than one that bounces.</summary>
    public bool Accept(IEnumerable<string> paths)
    {
        if (!IsVisible(Tab.Shelf)) return false;
        Shelf.Add(paths);
        Tab = Tab.Shelf;
        return true;
    }
}
