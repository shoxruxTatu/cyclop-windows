using System.Security.Cryptography;
using System.Text;

namespace Cyclop.Calendar;

public sealed record Meeting(
    string Id,
    string Title,
    DateTime Start,
    DateTime End,
    int FeedIndex,
    string FeedName,
    Uri? Link,
    string? Provider)
{
    public bool IsRunning(DateTime now) => Start <= now && now < End;
    public bool Overlaps(Meeting other) => Start < other.End && End > other.Start;
}

/// <summary>
/// The next meetings a week ahead, read from the calendar feeds listed in
/// Settings. Feeds are fetched every quarter hour while the tab is on the rail
/// and cached on disk, so the panel has an answer straight after launch and
/// while offline.
/// </summary>
public sealed class CalendarStore : Observable
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly TimeSpan Horizon = TimeSpan.FromDays(7);

    readonly ConfigStore config;
    readonly string cacheFolder;
    readonly Dictionary<string, string> texts = new();
    Repeating? refresh, tick;
    bool isActive;

    public List<Meeting> Meetings { get; private set; } = new();
    public DateTime Now { get; private set; } = DateTime.Now;
    /// <summary>Per feed, why its last fetch failed — shown under the list.</summary>
    public Dictionary<string, string> Failures { get; } = new();
    public bool IsLoading { get; private set; }

    public CalendarStore(ConfigStore config, string? cacheFolder = null)
    {
        this.config = config;
        this.cacheFolder = cacheFolder ?? Support.Directory("CalendarCache");
        config.Changed += () => { if (refresh is not null) Rebuild(); };
    }

    public bool HasFeeds => config.Calendars.Count > 0;

    public Meeting? Next => Meetings.FirstOrDefault(m => m.End > DateTime.Now);

    public IEnumerable<Meeting> Upcoming =>
        Next is { } next ? Meetings.Where(m => m.Id != next.Id && m.End > DateTime.Now) : Enumerable.Empty<Meeting>();

    public void Start()
    {
        LoadCache();
        Rebuild();
        refresh?.Dispose();
        refresh = new Repeating(TimeSpan.FromMinutes(15), () => _ = FetchAllAsync(), fireNow: true);
    }

    public void Stop()
    {
        refresh?.Dispose();
        refresh = null;
        tick?.Dispose();
        tick = null;
    }

    /// <summary>Panel open or closed. The half-minute tick serves eyes only.</summary>
    public void SetActive(bool active)
    {
        isActive = active;
        tick?.Dispose();
        tick = null;
        if (!active) return;
        Tick();
        tick = new Repeating(TimeSpan.FromSeconds(30), Tick);
    }

    DateTime lastFetch;

    /// <summary>Asked when the tab is shown and when a feed is added — at
    /// most once every two minutes, unless <paramref name="force"/>.</summary>
    public void Refresh(bool force = false)
    {
        if (!force && DateTime.Now - lastFetch < TimeSpan.FromMinutes(2)) return;
        _ = FetchAllAsync();
    }

    void Tick()
    {
        Now = DateTime.Now;
        Meetings.RemoveAll(m => m.End <= Now);
        Notify();
    }

    async Task FetchAllAsync()
    {
        lastFetch = DateTime.Now;
        var feeds = config.Calendars.Where(f => f.Enabled).ToList();
        if (feeds.Count == 0) { Rebuild(); return; }
        IsLoading = true;
        Notify();
        foreach (var feed in feeds)
        {
            try
            {
                var text = await ReadAsync(feed.Url);
                if (!text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(L10n.T("This link is not an iCal calendar."));
                texts[feed.Url] = text;
                Failures.Remove(feed.Url);
                try { Support.WriteAtomic(CachePath(feed.Url), text); } catch { }
            }
            catch (Exception e)
            {
                Failures[feed.Url] = e.Message;
            }
        }
        IsLoading = false;
        Rebuild();
    }

    /// <summary>A secret iCal address, a <c>webcal://</c> link, or a file on disk.</summary>
    static async Task<string> ReadAsync(string url)
    {
        var u = url.Trim();
        if (u.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) u = "https://" + u[9..];
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return await Http.GetStringAsync(u);
        if (u.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) u = new Uri(u).LocalPath;
        return await System.IO.File.ReadAllTextAsync(u);
    }

    string CachePath(string url)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
        return Path.Combine(cacheFolder, hash + ".ics");
    }

    void LoadCache()
    {
        foreach (var feed in config.Calendars)
        {
            if (texts.ContainsKey(feed.Url)) continue;
            try
            {
                var path = CachePath(feed.Url);
                if (System.IO.File.Exists(path)) texts[feed.Url] = System.IO.File.ReadAllText(path);
            }
            catch { }
        }
    }

    void Rebuild()
    {
        var now = DateTimeOffset.Now;
        var list = new List<Meeting>();
        var feeds = config.Calendars;
        for (int i = 0; i < feeds.Count; i++)
        {
            var feed = feeds[i];
            if (!feed.Enabled || !texts.TryGetValue(feed.Url, out var text)) continue;
            List<Occurrence> occurrences;
            try { occurrences = Ics.Expand(text, now, now + Horizon); }
            catch (Exception e) { Failures[feed.Url] = e.Message; continue; }
            foreach (var o in occurrences)
            {
                if (o.AllDay) continue;
                var link = MeetingLink.Find(o.Haystacks);
                list.Add(new Meeting(
                    o.Id,
                    string.IsNullOrWhiteSpace(o.Title) ? L10n.T("Untitled") : o.Title,
                    o.Start.LocalDateTime,
                    o.End.LocalDateTime,
                    i,
                    feed.Name,
                    link,
                    link is null ? null : MeetingLink.Provider(link)));
            }
        }
        // The same meeting in two subscribed calendars is one meeting.
        Meetings = list
            .GroupBy(m => (m.Title, m.Start, m.End)).Select(g => g.First())
            .OrderBy(m => m.Start).ToList();
        Now = DateTime.Now;
        Notify();
    }

    /// <summary>"in 12 min", "now", "tomorrow" — lower-case as the languages
    /// have it; the capital is added where the label is built.</summary>
    public static string Countdown(Meeting meeting, DateTime now)
    {
        if (meeting.IsRunning(now)) return L10n.T("now").SentenceCased();
        int minutes = (int)Math.Ceiling((meeting.Start - now).TotalMinutes);
        string phrase;
        if (minutes <= 0) phrase = L10n.T("any moment");
        else if (minutes < 60) phrase = L10n.T("in %d min", minutes);
        else
        {
            int hours = minutes / 60;
            if (hours < 24)
            {
                int rest = minutes % 60;
                phrase = rest == 0 ? L10n.T("in %d h", hours) : L10n.T("in %d h %d min", hours, rest);
            }
            else
            {
                int days = hours / 24;
                phrase = days == 1 ? L10n.T("tomorrow") : L10n.T("in %d d", days);
            }
        }
        return phrase.SentenceCased();
    }
}
