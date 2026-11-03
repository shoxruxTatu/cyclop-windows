using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cyclop.App;
using Cyclop.Calendar;
using Cyclop.UI;

namespace Cyclop.Panes;

/// <summary>
/// The next meeting a week ahead, with a one-click join for Zoom, Meet and
/// Teams; overlapping meetings each get their own button. Read from the iCal
/// feeds listed in Settings.
/// </summary>
internal sealed class CalendarPane : Pane
{
    readonly CalendarStore calendar;
    readonly Grid content;

    public CalendarPane(AppModel model, IPanelHost host) : base(model, host)
    {
        calendar = model.Calendar;
        content = new Grid();
        Children.Add(content);
        calendar.Changed += Rebuild;
        model.Privacy.Changed += Rebuild;
        model.Config.Changed += Rebuild;
    }

    protected override void OnShown() => Rebuild();

    void Rebuild()
    {
        var next = calendar.Next;
        HeaderText = next is null ? null : CalendarStore.Countdown(next, calendar.Now);
        if (!IsShown) return;
        content.Children.Clear();

        if (!calendar.HasFeeds)
        {
            content.Children.Add(Ui.Empty(Glyph.Calendar, L10n.T("See your next meetings"),
                L10n.T("Add your calendar's iCal link in Settings — Google Calendar: Settings → your calendar → Secret address in iCal format; Outlook: Settings → Shared calendars → Publish."),
                Ui.PillButton(L10n.T("Open Settings"), () => Host.Select(Tab.Settings), Glyph.Settings)));
            return;
        }
        if (next is null)
        {
            var caption = calendar.IsLoading ? L10n.T("Loading…") : Failures();
            content.Children.Add(Ui.Empty(Glyph.Calendar, L10n.T("No more meetings"), caption));
            return;
        }

        var layout = new DockPanel();
        var top = NextCard(next);
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);

        var failures = Failures();
        if (failures is not null)
        {
            var notice = Ui.Label(failures, 9.5, Theme.Warning);
            DockPanel.SetDock(notice, Dock.Bottom);
            layout.Children.Add(notice);
        }

        var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var upcoming = calendar.Upcoming.Where(m => !m.Overlaps(next)).ToList();
        if (upcoming.Count == 0)
        {
            var none = Ui.Label(L10n.T("No other meetings this week"), 10.5, Theme.Tertiary);
            none.Margin = new Thickness(4, 4, 0, 0);
            rows.Children.Add(none);
        }
        foreach (var meeting in upcoming) rows.Children.Add(Row(meeting));
        layout.Children.Add(Ui.Scroller(rows));
        content.Children.Add(layout);
    }

    string? Failures()
    {
        if (calendar.Failures.Count == 0) return null;
        var feeds = Model.Config.Calendars;
        return string.Join("  ·  ", calendar.Failures.Select(f =>
        {
            var name = feeds.FirstOrDefault(c => c.Url == f.Key)?.Name;
            return $"{(string.IsNullOrEmpty(name) ? L10n.T("Calendar") : name)}: {f.Value}";
        }));
    }

    UIElement NextCard(Meeting next)
    {
        var bar = new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = Color(next), Margin = new Thickness(0, 0, 10, 0) };
        var title = Ui.Label(next.Title, 15, Theme.White, FontWeights.SemiBold);
        var when = Ui.Label($"{Day(next.Start)}  {Span(next)}", 11, Theme.Secondary);
        var countdown = Ui.Label(CalendarStore.Countdown(next, calendar.Now), 11, next.IsRunning(calendar.Now) ? Theme.White : Theme.Tertiary, FontWeights.Medium);
        countdown.Margin = new Thickness(10, 0, 0, 0);
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        meta.Children.Add(when);
        meta.Children.Add(countdown);
        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(meta);

        // The next meeting and everything overlapping it: two calls at once
        // each get their own button rather than one hiding the other.
        var joins = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var concurrent = new[] { next }.Concat(calendar.Upcoming.Where(m => m.Overlaps(next))).Where(m => m.Link is not null).ToList();
        foreach (var m in concurrent)
        {
            var label = concurrent.Count > 1 || m != next ? $"{L10n.T("Join")} · {Short(m.Title)}" :
                m.Provider is { } p ? L10n.T("Join · %@", p) : L10n.T("Join");
            var join = Ui.PillButton(label, () => Join(m), Glyph.Video, accent: true);
            join.Margin = new Thickness(6, 0, 0, 0);
            joins.Children.Add(join);
        }

        var line = new DockPanel();
        DockPanel.SetDock(bar, Dock.Left);
        DockPanel.SetDock(joins, Dock.Right);
        line.Children.Add(bar);
        line.Children.Add(joins);
        line.Children.Add(text);

        var id = "meeting." + next.Id;
        bool hidden = Model.Privacy.Hides(PrivacySection.Calendar, id);
        return Ui.Card(Cover.Wrap(line, hidden, () => Model.Privacy.Toggle(id), 10), 12, new Thickness(10, 9, 10, 9));
    }

    UIElement Row(Meeting meeting)
    {
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Color(meeting), Margin = new Thickness(2, 0, 8, 0) };
        var when = Ui.Label($"{Day(meeting.Start)} {meeting.Start.ToString("t", L10n.Culture)}", 10.5, Theme.Tertiary);
        when.Width = 120;
        var title = Ui.Label(meeting.Title, 11.5, Theme.White);
        var line = new DockPanel { Height = 24 };
        DockPanel.SetDock(dot, Dock.Left);
        DockPanel.SetDock(when, Dock.Left);
        line.Children.Add(dot);
        line.Children.Add(when);
        if (meeting.Link is not null)
        {
            var join = Ui.IconButton(Glyph.Video, () => Join(meeting), 22, 10, tip: meeting.Provider is { } p ? L10n.T("Join · %@", p) : L10n.T("Join"));
            DockPanel.SetDock(join, Dock.Right);
            line.Children.Add(join);
        }
        line.Children.Add(title);
        var id = "meeting." + meeting.Id;
        return Cover.Wrap(line, Model.Privacy.Hides(PrivacySection.Calendar, id), () => Model.Privacy.Toggle(id));
    }

    Brush Color(Meeting m) => Theme.FeedColors[m.FeedIndex % Theme.FeedColors.Length];

    static string Short(string title) => title.Length > 18 ? title[..17] + "…" : title;

    static string Span(Meeting m) => $"{m.Start.ToString("t", L10n.Culture)}–{m.End.ToString("t", L10n.Culture)}";

    /// <summary>"Today", "Tomorrow", then the weekday and date.</summary>
    static string Day(DateTime date)
    {
        var today = DateTime.Today;
        if (date.Date == today) return L10n.T("Today");
        if (date.Date == today.AddDays(1)) return L10n.T("Tomorrow");
        return date.ToString("ddd d MMM", L10n.Culture).SentenceCased();
    }

    /// <summary>Checked a second time, at the point of opening: the link came
    /// out of an event, and an event can be sent by anyone.</summary>
    static void Join(Meeting meeting)
    {
        if (meeting.Link is not { } link || !MeetingLink.IsJoinable(link)) return;
        try { Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }
}
