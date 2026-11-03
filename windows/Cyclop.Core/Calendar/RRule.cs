using System.Globalization;

namespace Cyclop.Calendar;

/// <summary>
/// The recurrence rules calendars actually write. Occurrences are produced in
/// the event's own wall-clock time, so a 9:00 meeting stays at 9:00 across a
/// daylight-saving change — the conversion to a moment happens afterwards.
/// </summary>
internal sealed class RRule
{
    public string Freq = "";
    public int Interval = 1;
    public int? Count;
    public Ics.Time? Until;
    /// <summary>Weekday with an optional ordinal: <c>2TU</c> is the second
    /// Tuesday, <c>-1FR</c> the last Friday, <c>MO</c> every Monday.</summary>
    public List<(int Ordinal, DayOfWeek Day)> ByDay = new();
    public List<int> ByMonthDay = new();
    public List<int> ByMonth = new();
    public int? BySetPos;

    /// <summary>A guard against a feed that asks for something unbounded.</summary>
    const int IterationCap = 50_000;

    public static RRule? Parse(string text)
    {
        var rule = new RRule();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].ToUpperInvariant();
            var value = part[(eq + 1)..];
            switch (key)
            {
                case "FREQ": rule.Freq = value.ToUpperInvariant(); break;
                case "INTERVAL": rule.Interval = Math.Max(1, int.TryParse(value, out var i) ? i : 1); break;
                case "COUNT": rule.Count = int.TryParse(value, out var c) ? c : null; break;
                case "UNTIL": rule.Until = Ics.ParseTime(value, new Dictionary<string, string>()); break;
                case "BYDAY":
                    foreach (var d in value.Split(','))
                        if (ParseDay(d) is { } day) rule.ByDay.Add(day);
                    break;
                case "BYMONTHDAY":
                    rule.ByMonthDay.AddRange(value.Split(',').Select(v => int.TryParse(v, out var n) ? n : 0).Where(n => n != 0));
                    break;
                case "BYMONTH":
                    rule.ByMonth.AddRange(value.Split(',').Select(v => int.TryParse(v, out var n) ? n : 0).Where(n => n is >= 1 and <= 12));
                    break;
                case "BYSETPOS":
                    rule.BySetPos = int.TryParse(value.Split(',')[0], out var p) ? p : null;
                    break;
            }
        }
        return rule.Freq is "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY" ? rule : null;
    }

    static (int, DayOfWeek)? ParseDay(string text)
    {
        var t = text.Trim().ToUpperInvariant();
        if (t.Length < 2) return null;
        var code = t[^2..];
        DayOfWeek? day = code switch
        {
            "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
            "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday,
            "SU" => DayOfWeek.Sunday, _ => null,
        };
        if (day is null) return null;
        int ordinal = t.Length > 2 && int.TryParse(t[..^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return (ordinal, day.Value);
    }

    /// <summary>
    /// Every occurrence start from <paramref name="start"/> up to
    /// <paramref name="windowEnd"/>, in wall time. Without a COUNT, whole
    /// periods before <paramref name="windowStart"/> are skipped arithmetically
    /// rather than walked: a daily stand-up created years ago would otherwise
    /// be thousands of steps away from this week.
    /// </summary>
    public IEnumerable<DateTime> Walls(DateTime start, DateTime windowStart, DateTime windowEnd, bool allDay, DateTime? until)
    {
        int produced = 0;
        int iterations = 0;
        long period = 0;
        if (Count is null) period = Math.Max(0, PeriodsBefore(start, windowStart) - 1);

        while (iterations++ < IterationCap)
        {
            var candidates = Candidates(start, period);
            if (candidates is null) yield break;
            bool beyond = false;
            foreach (var wall in candidates)
            {
                if (wall < start) continue;
                if (until is { } u && wall > u) yield break;
                if (Count is { } count && produced >= count) yield break;
                if (wall > windowEnd) { beyond = true; break; }
                produced++;
                yield return wall;
            }
            if (beyond) yield break;
            // A period whose first candidate is already past the window ends it
            // even when the period produced nothing (e.g. an empty month).
            if (PeriodStart(start, period) > windowEnd) yield break;
            period++;
        }
    }

    long PeriodsBefore(DateTime start, DateTime windowStart)
    {
        if (windowStart <= start) return 0;
        return Freq switch
        {
            "DAILY" => (long)((windowStart - start).TotalDays / Interval),
            "WEEKLY" => (long)((windowStart - start).TotalDays / (7.0 * Interval)),
            "MONTHLY" => ((windowStart.Year - start.Year) * 12 + windowStart.Month - start.Month) / Interval,
            "YEARLY" => (windowStart.Year - start.Year) / Interval,
            _ => 0,
        };
    }

    DateTime PeriodStart(DateTime start, long period) => Freq switch
    {
        "DAILY" => start.AddDays(period * Interval),
        "WEEKLY" => WeekStart(start).AddDays(7 * period * Interval),
        "MONTHLY" => new DateTime(start.Year, start.Month, 1).AddMonths((int)(period * Interval)),
        _ => new DateTime(start.Year, 1, 1).AddYears((int)(period * Interval)),
    };

    static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    List<DateTime>? Candidates(DateTime start, long period)
    {
        var time = start.TimeOfDay;
        var list = new List<DateTime>();
        switch (Freq)
        {
            case "DAILY":
            {
                var day = start.AddDays(period * Interval);
                if (Matches(day)) list.Add(day);
                break;
            }
            case "WEEKLY":
            {
                var weekStart = WeekStart(start).AddDays(7 * period * Interval);
                var days = ByDay.Count > 0 ? ByDay.Select(d => d.Day).Distinct() : new[] { start.DayOfWeek };
                foreach (var day in days.OrderBy(d => ((int)d + 6) % 7))
                    list.Add(weekStart.AddDays(((int)day + 6) % 7) + time);
                break;
            }
            case "MONTHLY":
            {
                var month = new DateTime(start.Year, start.Month, 1).AddMonths((int)(period * Interval));
                if (month.Year > 9000) return null;
                list.AddRange(DaysInMonth(month, start).Select(d => d + time));
                break;
            }
            case "YEARLY":
            {
                var year = start.Year + (int)(period * Interval);
                if (year > 9000) return null;
                var months = ByMonth.Count > 0 ? ByMonth : new List<int> { start.Month };
                foreach (var m in months.OrderBy(m => m))
                {
                    var month = new DateTime(year, m, 1);
                    if (ByDay.Count == 0 && ByMonthDay.Count == 0)
                    {
                        if (start.Day <= DateTime.DaysInMonth(year, m)) list.Add(new DateTime(year, m, start.Day) + time);
                    }
                    else list.AddRange(DaysInMonth(month, start).Select(d => d + time));
                }
                break;
            }
            default:
                return null;
        }
        if (BySetPos is { } pos && list.Count > 0)
        {
            int index = pos > 0 ? pos - 1 : list.Count + pos;
            list = index >= 0 && index < list.Count ? new List<DateTime> { list[index] } : new List<DateTime>();
        }
        return list;
    }

    bool Matches(DateTime day) =>
        (ByDay.Count == 0 || ByDay.Any(d => d.Day == day.DayOfWeek)) &&
        (ByMonth.Count == 0 || ByMonth.Contains(day.Month)) &&
        (ByMonthDay.Count == 0 || ByMonthDay.Contains(day.Day));

    /// <summary>The days of one month the rule picks, in order.</summary>
    IEnumerable<DateTime> DaysInMonth(DateTime month, DateTime start)
    {
        int length = DateTime.DaysInMonth(month.Year, month.Month);
        var days = new SortedSet<int>();
        if (ByMonthDay.Count > 0)
        {
            foreach (var d in ByMonthDay)
            {
                int day = d > 0 ? d : length + d + 1;
                if (day >= 1 && day <= length) days.Add(day);
            }
        }
        else if (ByDay.Count > 0)
        {
            foreach (var (ordinal, weekday) in ByDay)
            {
                var matching = Enumerable.Range(1, length)
                    .Where(d => new DateTime(month.Year, month.Month, d).DayOfWeek == weekday).ToList();
                if (ordinal == 0) foreach (var d in matching) days.Add(d);
                else
                {
                    int index = ordinal > 0 ? ordinal - 1 : matching.Count + ordinal;
                    if (index >= 0 && index < matching.Count) days.Add(matching[index]);
                }
            }
        }
        else if (start.Day <= length) days.Add(start.Day);
        return days.Select(d => new DateTime(month.Year, month.Month, d));
    }
}
