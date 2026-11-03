using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cyclop;

public sealed class TeleprompterConfig
{
    public double Speed { get; set; } = 1;
    public double FontSize { get; set; } = 30;
}

/// <summary>One calendar subscription: a secret iCal address, or a local .ics.</summary>
public sealed class CalendarFeed
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>Global shortcuts for the screenshot tool, written the way they
/// read: <c>"Ctrl+Alt+A"</c>. Empty is off.</summary>
public sealed class HotkeyConfig
{
    /// <summary>Ctrl+Alt+A is what several messengers use for their own area
    /// snip, so the hand already knows it; Ctrl+Shift combinations collide with
    /// "Save as" and friends in half the apps on the PC.</summary>
    public string Area { get; set; } = "Ctrl+Alt+A";
    public string Screen { get; set; } = "Ctrl+Alt+S";
}

/// <summary>The shape of <c>config.json</c>. Every property has a default, so
/// a file written by an older version — without the newest keys — still
/// reads.</summary>
public sealed class ConfigFile
{
    public bool ShowOnAllDisplays { get; set; } = true;
    public bool SaveClipboardImages { get; set; } = true;
    public List<string> Privacy { get; set; } = new();
    public TeleprompterConfig Teleprompter { get; set; } = new();
    public List<string> HiddenTabs { get; set; } = new();
    public double OpenDelay { get; set; } = 0.2;
    public bool ShowTrayIcon { get; set; } = true;
    /// <summary>Newer versions install themselves; off, Settings offers them.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Whether the update check carries the anonymous install id the
    /// author counts users by. Off, the check still runs — without it.</summary>
    public bool AnonymousStats { get; set; } = true;
    /// <summary>The folder watched for new screenshots; <c>null</c> is off.</summary>
    public string? ScreenshotFolder { get; set; }
    public List<CalendarFeed> Calendars { get; set; } = new();
    public HotkeyConfig Hotkeys { get; set; } = new();

    internal void Normalize()
    {
        Hotkeys ??= new();
        Hotkeys.Area ??= "";
        Hotkeys.Screen ??= "";
        Privacy ??= new();
        Teleprompter ??= new();
        HiddenTabs ??= new();
        Calendars ??= new();
        Calendars.RemoveAll(c => c is null || string.IsNullOrWhiteSpace(c.Url));
    }
}

/// <summary>
/// Everything that makes sense on somebody else's PC, in one hand-editable
/// file next to <c>snippets.json</c> and <c>notes.json</c>.
///
/// A file that exists but cannot be parsed raises <see cref="FileBroken"/>,
/// and from then on nothing is written: "could not read" and "read as it is"
/// are different answers, and only the second makes writing back safe.
/// </summary>
public sealed class ConfigStore : Observable
{
    static ConfigStore? shared;
    public static ConfigStore Shared => shared ??= new ConfigStore(Support.File("config.json"));

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // Cyrillic and slashes left readable: the file is meant to be opened.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string FilePath { get; }
    public bool FileBroken { get; private set; }
    ConfigFile value;

    public ConfigStore(string path)
    {
        FilePath = path;
        if (System.IO.File.Exists(path))
        {
            try
            {
                value = JsonSerializer.Deserialize<ConfigFile>(System.IO.File.ReadAllText(path), Json) ?? new();
                value.Normalize();
            }
            catch (Exception e)
            {
                value = new ConfigFile();
                FileBroken = true;
                Support.Log("config.json is not readable: " + e.Message);
            }
        }
        else
        {
            value = new ConfigFile();
            Write();
        }
    }

    public bool ShowOnAllDisplays { get => value.ShowOnAllDisplays; set { this.value.ShowOnAllDisplays = value; Persist(); } }
    public bool SaveClipboardImages { get => value.SaveClipboardImages; set { this.value.SaveClipboardImages = value; Persist(); } }
    public bool ShowTrayIcon { get => value.ShowTrayIcon; set { this.value.ShowTrayIcon = value; Persist(); } }
    public bool AutoUpdate { get => value.AutoUpdate; set { this.value.AutoUpdate = value; Persist(); } }
    public bool AnonymousStats { get => value.AnonymousStats; set { this.value.AnonymousStats = value; Persist(); } }
    public IReadOnlyList<string> Privacy { get => value.Privacy; set { this.value.Privacy = value.ToList(); Persist(); } }
    public IReadOnlyList<string> HiddenTabs { get => value.HiddenTabs; set { this.value.HiddenTabs = value.ToList(); Persist(); } }
    public double TeleprompterSpeed { get => value.Teleprompter.Speed; set { this.value.Teleprompter.Speed = value; Persist(); } }
    public double TeleprompterFontSize { get => value.Teleprompter.FontSize; set { this.value.Teleprompter.FontSize = value; Persist(); } }
    public string? ScreenshotFolder { get => value.ScreenshotFolder; set { this.value.ScreenshotFolder = value; Persist(); } }
    public string HotkeyArea { get => value.Hotkeys.Area; set { this.value.Hotkeys.Area = value; Persist(); } }
    public string HotkeyScreen { get => value.Hotkeys.Screen; set { this.value.Hotkeys.Screen = value; Persist(); } }

    public static readonly (double Min, double Max) OpenDelayRange = (0.05, 0.6);

    /// <summary>Clamped on the way out: the file is edited by hand too, and
    /// <c>10</c> typed for milliseconds is ten seconds — a panel that never
    /// opens, guarding the one switch that could fix it.</summary>
    public double OpenDelay
    {
        get => Math.Clamp(value.OpenDelay, OpenDelayRange.Min, OpenDelayRange.Max);
        set { this.value.OpenDelay = value; Persist(); }
    }

    public IReadOnlyList<CalendarFeed> Calendars => value.Calendars;

    public void AddCalendar(string name, string url)
    {
        value.Calendars.Add(new CalendarFeed { Name = name, Url = url });
        Persist();
    }

    public void RemoveCalendar(CalendarFeed feed)
    {
        value.Calendars.Remove(feed);
        Persist();
    }

    public void SetCalendarEnabled(CalendarFeed feed, bool enabled)
    {
        feed.Enabled = enabled;
        Persist();
    }

    void Persist()
    {
        Write();
        Notify();
    }

    void Write()
    {
        if (FileBroken) return;
        try { Support.WriteAtomic(FilePath, JsonSerializer.Serialize(value, Json)); }
        catch (Exception e) { Support.Log("cannot write config.json: " + e.Message); }
    }

    /// <summary>Makes sure there is a file to point Explorer at.</summary>
    public void EnsureFile()
    {
        if (!System.IO.File.Exists(FilePath)) Write();
    }
}

/// <summary>
/// What belongs to this PC rather than to the person: shelf paths (content,
/// not configuration) and the currency pair. The Mac keeps these in
/// <c>UserDefaults</c>; here it is <c>state.json</c>, which nobody needs to
/// open.
/// </summary>
public sealed class AppState
{
    public List<string> Shelf { get; set; } = new();
    public string? CurrencySource { get; set; }
    public string? CurrencyTarget { get; set; }
    /// <summary>A random id made on first launch — not derived from anything
    /// on the PC, so it says nothing about who or where.</summary>
    public string? InstallId { get; set; }
    public bool StatsNoticeShown { get; set; }

    public string EnsureInstallId()
    {
        if (!Guid.TryParse(InstallId, out _))
        {
            InstallId = Guid.NewGuid().ToString();
            Save();
        }
        return InstallId!;
    }

    static AppState? shared;
    static string FilePath => Support.File("state.json");

    public static AppState Shared => shared ??= Load();

    static AppState Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                var state = JsonSerializer.Deserialize<AppState>(System.IO.File.ReadAllText(FilePath), ConfigStore.Json);
                if (state is not null)
                {
                    state.Shelf ??= new();
                    return state;
                }
            }
        }
        catch (Exception e) { Support.Log("state.json is not readable: " + e.Message); }
        return new AppState();
    }

    public void Save()
    {
        try { Support.WriteAtomic(FilePath, JsonSerializer.Serialize(this, ConfigStore.Json)); }
        catch (Exception e) { Support.Log("cannot write state.json: " + e.Message); }
    }
}
