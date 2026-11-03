using System.Globalization;
using System.Text;

namespace Cyclop.Calendar;

/// <summary>One occurrence of an event inside the asked-for window.</summary>
public sealed record Occurrence(
    string Id,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    IReadOnlyList<string> Haystacks);

/// <summary>
/// A reader for iCalendar feeds (RFC 5545) — the secret iCal address Google
/// Calendar and Outlook.com hand out, or an .ics exported from anywhere.
///
/// Windows does not let an ordinary desktop app read the system calendar
/// without being packaged, so where the Mac asks EventKit, this reads the feed
/// the calendar service already publishes. Enough of the format to answer
/// "what is next this week": single events, the common recurrence rules
/// (daily, weekly by weekday, monthly by day or by nth weekday, yearly), their
/// exceptions and moved instances, and time zones by name.
/// </summary>
public static class Ics
{
    /// <summary>Occurrences overlapping [from, to), cancelled ones left out.</summary>
    public static List<Occurrence> Expand(string text, DateTimeOffset from, DateTimeOffset to)
    {
        var calendar = Parse(text);
        var result = new List<Occurrence>();

        // Moved or cancelled instances, keyed by the series and the start they replace.
        var overrides = new HashSet<string>();
        foreach (var e in calendar.Events.Where(e => e.RecurrenceId is not null))
            overrides.Add(Key(e.Uid, calendar.Instant(e.RecurrenceId!.Value)));

        foreach (var e in calendar.Events)
        {
            if (e.Start is not { } start) continue;
            bool cancelled = string.Equals(e.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase);
            var startInstant = calendar.Instant(start);
            var length = EventLength(calendar, e, start, startInstant);

            if (e.RecurrenceId is not null || e.RRule is null)
            {
                if (cancelled) continue;
                var end = startInstant + length;
                if (end > from && startInstant < to)
                    result.Add(Make(e, startInstant, end, start.IsDate));
                continue;
            }
            if (cancelled) continue;

            var rule = RRule.Parse(e.RRule);
            if (rule is null) continue;
            var excluded = new HashSet<DateTimeOffset>(e.ExDates.Select(calendar.Instant));
            var excludedDays = new HashSet<DateTime>(e.ExDates.Where(x => x.IsDate).Select(x => x.Wall.Date));
            var windowStartWall = calendar.WallIn(start, from - length);
            var windowEndWall = calendar.WallIn(start, to);

            foreach (var wall in rule.Walls(start.Wall, windowStartWall, windowEndWall, start.IsDate, calendar.UntilWall(start, rule)))
            {
                var t = start with { Wall = wall };
                var instant = calendar.Instant(t);
                if (excluded.Contains(instant) || excludedDays.Contains(wall.Date)) continue;
                if (overrides.Contains(Key(e.Uid, instant))) continue;
                var end = instant + length;
                if (end > from && instant < to) result.Add(Make(e, instant, end, start.IsDate));
            }
        }
        result.Sort((a, b) => a.Start.CompareTo(b.Start));
        return result;
    }

    static string Key(string uid, DateTimeOffset instant) => uid + "|" + instant.UtcTicks;

    static TimeSpan EventLength(Calendar calendar, Event e, Time start, DateTimeOffset startInstant)
    {
        if (e.End is { } end) return calendar.Instant(end) - startInstant;
        if (e.Duration is { } duration) return duration;
        return start.IsDate ? TimeSpan.FromDays(1) : TimeSpan.Zero;
    }

    static Occurrence Make(Event e, DateTimeOffset start, DateTimeOffset end, bool allDay)
    {
        var haystacks = new[] { e.Location, e.Conference, e.Description, e.Url }
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        return new Occurrence(e.Uid + "|" + start.UtcTicks, e.Summary, start, end, allDay, haystacks);
    }

    // MARK: - Model

    /// <summary>A moment as written: wall-clock time plus how to read it — in
    /// UTC, in a named zone, floating (the reader's own zone), or a bare date.</summary>
    internal readonly record struct Time(DateTime Wall, string? ZoneId, bool IsUtc, bool IsDate);

    internal sealed class Event
    {
        public string Uid = "";
        public string Summary = "";
        public Time? Start, End, RecurrenceId;
        public TimeSpan? Duration;
        public string? Location, Description, Url, Status, Conference, RRule;
        public List<Time> ExDates = new();
    }

    internal sealed class Calendar
    {
        public List<Event> Events = new();
        /// <summary>Standard-time offsets from VTIMEZONE blocks, for zone names
        /// Windows does not know (Outlook writes some of its own).</summary>
        public Dictionary<string, TimeSpan> DeclaredOffsets = new();
        readonly Dictionary<string, TimeZoneInfo> zones = new();

        public TimeZoneInfo Zone(Time t)
        {
            if (t.IsUtc) return TimeZoneInfo.Utc;
            if (t.ZoneId is null) return TimeZoneInfo.Local;
            if (zones.TryGetValue(t.ZoneId, out var cached)) return cached;
            var zone = Resolve(t.ZoneId);
            zones[t.ZoneId] = zone;
            return zone;
        }

        TimeZoneInfo Resolve(string id)
        {
            var clean = id.Trim('"', ' ');
            if (clean.StartsWith('/')) clean = clean.TrimStart('/');
            try { return TimeZoneInfo.FindSystemTimeZoneById(clean); } catch { }
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(clean, out var windowsId))
                try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); } catch { }
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(clean, out var ianaId))
                try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); } catch { }
            if (DeclaredOffsets.TryGetValue(id, out var offset))
                return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
            return TimeZoneInfo.Local;
        }

        public DateTimeOffset Instant(Time t)
        {
            var wall = DateTime.SpecifyKind(t.Wall, DateTimeKind.Unspecified);
            if (t.IsUtc) return new DateTimeOffset(wall, TimeSpan.Zero);
            var zone = Zone(t);
            // A wall time skipped by a DST jump has no offset of its own;
            // reading it an hour later is what calendars do.
            if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1);
            return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }

        /// <summary>The same moment as wall time in the zone <paramref name="like"/> is written in.</summary>
        public DateTime WallIn(Time like, DateTimeOffset instant)
        {
            if (like.IsUtc) return instant.UtcDateTime;
            return TimeZoneInfo.ConvertTime(instant, Zone(like)).DateTime;
        }

        public DateTime? UntilWall(Time start, RRule rule) =>
            rule.Until is { } until ? (until.IsDate ? until.Wall.Date.AddDays(1).AddTicks(-1) : WallIn(start, Instant(until))) : null;
    }

    // MARK: - Parsing

    internal static Calendar Parse(string text)
    {
        var calendar = new Calendar();
        Event? current = null;
        var stack = new Stack<string>();
        string? tzid = null;
        TimeSpan? standardOffset = null;

        foreach (var line in Unfold(text))
        {
            var (name, parameters, value) = Split(line);
            if (name == "BEGIN")
            {
                var block = value.ToUpperInvariant();
                stack.Push(block);
                if (block == "VEVENT") current = new Event();
                if (block == "VTIMEZONE") { tzid = null; standardOffset = null; }
                continue;
            }
            if (name == "END")
            {
                var block = value.ToUpperInvariant();
                if (stack.Count > 0) stack.Pop();
                if (block == "VEVENT" && current is not null) { calendar.Events.Add(current); current = null; }
                if (block == "VTIMEZONE" && tzid is not null && standardOffset is { } o) calendar.DeclaredOffsets[tzid] = o;
                continue;
            }
            var where = stack.Count > 0 ? stack.Peek() : "";

            if (where == "VTIMEZONE" && name == "TZID") tzid = value;
            if (where == "STANDARD" && name == "TZOFFSETTO" && ParseOffset(value) is { } offset) standardOffset = offset;

            if (current is null || where != "VEVENT") continue;
            switch (name)
            {
                case "UID": current.Uid = value; break;
                case "SUMMARY": current.Summary = UnescapeText(value); break;
                case "LOCATION": current.Location = UnescapeText(value); break;
                case "DESCRIPTION": current.Description = UnescapeText(value); break;
                case "URL": current.Url = value; break;
                case "STATUS": current.Status = value; break;
                case "RRULE": current.RRule = value; break;
                case "DTSTART": current.Start = ParseTime(value, parameters); break;
                case "DTEND": current.End = ParseTime(value, parameters); break;
                case "DURATION": current.Duration = ParseDuration(value); break;
                case "RECURRENCE-ID": current.RecurrenceId = ParseTime(value, parameters); break;
                case "EXDATE":
                    foreach (var part in value.Split(','))
                        if (ParseTime(part, parameters) is { } ex) current.ExDates.Add(ex);
                    break;
                // Where Google puts the Meet link, and Outlook the Teams one.
                case "X-GOOGLE-CONFERENCE":
                case "X-MICROSOFT-SKYPETEAMSMEETINGURL":
                case "X-MICROSOFT-ONLINEMEETINGCONFLINK":
                    current.Conference = value;
                    break;
            }
        }
        return calendar;
    }

    /// <summary>Long lines are folded with a leading space or tab.</summary>
    internal static IEnumerable<string> Unfold(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t'))
            {
                sb.Append(raw, 1, raw.Length - 1);
                continue;
            }
            if (sb.Length > 0) yield return sb.ToString();
            sb.Clear();
            sb.Append(raw);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary><c>NAME;PARAM=a;PARAM2="b:c":value</c> — the first colon outside quotes.</summary>
    internal static (string Name, Dictionary<string, string> Parameters, string Value) Split(string line)
    {
        bool quoted = false;
        int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        var head = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        var parts = head.Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq > 0) parameters[p[..eq]] = p[(eq + 1)..].Trim('"');
        }
        return (parts[0].ToUpperInvariant(), parameters, value);
    }

    static string UnescapeText(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                sb.Append(value[i] is 'n' or 'N' ? '\n' : value[i]);
            }
            else sb.Append(value[i]);
        }
        return sb.ToString();
    }

    internal static Time? ParseTime(string value, Dictionary<string, string> parameters)
    {
        var v = value.Trim();
        bool isDate = (parameters.TryGetValue("VALUE", out var kind) && kind.Equals("DATE", StringComparison.OrdinalIgnoreCase))
                      || (v.Length == 8 && !v.Contains('T'));
        if (isDate)
        {
            return DateTime.TryParseExact(v[..Math.Min(8, v.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? new Time(day, null, false, true) : null;
        }
        bool utc = v.EndsWith('Z');
        var body = utc ? v[..^1] : v;
        if (!DateTime.TryParseExact(body, new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
            return null;
        parameters.TryGetValue("TZID", out var zone);
        return new Time(wall, utc ? null : zone, utc, false);
    }

    /// <summary><c>P1W</c>, <c>PT1H30M</c>, <c>P1DT2H</c>, with an optional sign.</summary>
    internal static TimeSpan? ParseDuration(string value)
    {
        var v = value.Trim().ToUpperInvariant();
        int sign = 1;
        if (v.StartsWith('-')) { sign = -1; v = v[1..]; }
        else if (v.StartsWith('+')) v = v[1..];
        if (!v.StartsWith('P')) return null;
        var total = TimeSpan.Zero;
        var number = new StringBuilder();
        bool time = false;
        foreach (var c in v[1..])
        {
            if (char.IsDigit(c)) { number.Append(c); continue; }
            if (c == 'T') { time = true; continue; }
            if (number.Length == 0) return null;
            int n = int.Parse(number.ToString(), CultureInfo.InvariantCulture);
            number.Clear();
            total += c switch
            {
                'W' => TimeSpan.FromDays(7 * n),
                'D' => TimeSpan.FromDays(n),
                'H' when time => TimeSpan.FromHours(n),
                'M' when time => TimeSpan.FromMinutes(n),
                'S' when time => TimeSpan.FromSeconds(n),
                _ => TimeSpan.Zero,
            };
        }
        return sign * total;
    }

    static TimeSpan? ParseOffset(string value)
    {
        var v = value.Trim();
        if (v.Length < 5) return null;
        int sign = v[0] == '-' ? -1 : 1;
        if (!int.TryParse(v.AsSpan(1, 2), out var h) || !int.TryParse(v.AsSpan(3, 2), out var m)) return null;
        return sign * new TimeSpan(h, m, 0);
    }
}
