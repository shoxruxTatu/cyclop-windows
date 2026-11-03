namespace Cyclop;

public enum Tab
{
    Media, Shelf, Clipboard, Snippets, Calendar, Translate, Currency, Notes, Teleprompter, Utilities, Settings,
}

public static class Tabs
{
    /// <summary>The left rail carries the original six; growth continues in a
    /// second column on the right, opened by the notes — the daily tab of that
    /// column — with Settings last, furthest from the tabs people rest on.</summary>
    public static readonly Tab[] LeftRail = { Tab.Media, Tab.Shelf, Tab.Clipboard, Tab.Snippets, Tab.Calendar, Tab.Translate };
    public static readonly Tab[] RightRail = { Tab.Notes, Tab.Currency, Tab.Teleprompter, Tab.Utilities, Tab.Settings };

    /// <summary>Stable names, as stored in <c>config.json</c> — the same
    /// spelling the Mac version writes, so the file travels between them.</summary>
    public static string Id(this Tab tab) => tab switch
    {
        Tab.Media => "media",
        Tab.Shelf => "shelf",
        Tab.Clipboard => "clipboard",
        Tab.Snippets => "snippets",
        Tab.Calendar => "calendar",
        Tab.Translate => "translate",
        Tab.Currency => "currency",
        Tab.Notes => "notes",
        Tab.Teleprompter => "teleprompter",
        Tab.Utilities => "utilities",
        _ => "settings",
    };

    public static Tab? FromId(string id) =>
        Enum.GetValues<Tab>().Cast<Tab?>().FirstOrDefault(t => t!.Value.Id() == id);

    public static string Title(this Tab tab) => L10n.T(tab switch
    {
        Tab.Media => "Music",
        Tab.Shelf => "Shelf",
        Tab.Clipboard => "Clipboard",
        Tab.Snippets => "Snippets",
        Tab.Calendar => "Calendar",
        Tab.Translate => "Translate",
        Tab.Currency => "Currency",
        Tab.Notes => "Notes",
        Tab.Teleprompter => "Teleprompter",
        Tab.Utilities => "Utilities",
        _ => "Settings",
    });

    /// <summary>Tabs with a field in them. Landing on one hands it the
    /// keyboard, so that arriving and typing is a single move.</summary>
    public static bool NeedsKeyboard(this Tab tab) =>
        tab is Tab.Translate or Tab.Currency or Tab.Snippets or Tab.Notes;

    /// <summary>The teleprompter has to fit a paragraph; settings is a list
    /// somebody opens to go through the whole of.</summary>
    public static bool WantsTallBody(this Tab tab) => tab is Tab.Teleprompter or Tab.Settings;

    /// <summary>Every tab can leave the rail except the one the switches live on.</summary>
    public static bool CanHide(this Tab tab) => tab != Tab.Settings;
}
