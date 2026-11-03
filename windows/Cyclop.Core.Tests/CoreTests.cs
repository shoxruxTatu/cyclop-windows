using Cyclop;
using Cyclop.Calendar;
using Xunit;

namespace Cyclop.Tests;

public class IcsTests
{
    static string Wrap(string events) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\n" + events + "END:VCALENDAR\r\n";

    static readonly DateTimeOffset From = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset To = From.AddDays(7);

    [Fact]
    public void SingleUtcEvent()
    {
        var ics = Wrap("BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:Standup\r\nDTSTART:20261103T090000Z\r\nDTEND:20261103T091500Z\r\nEND:VEVENT\r\n");
        var list = Ics.Expand(ics, From, To);
        var o = Assert.Single(list);
        Assert.Equal("Standup", o.Title);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), o.Start);
        Assert.Equal(TimeSpan.FromMinutes(15), o.End - o.Start);
    }

    [Fact]
    public void ZonedEventConvertsByZone()
    {
        var ics = Wrap("BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:X\r\nDTSTART;TZID=Europe/Moscow:20261103T120000\r\nDURATION:PT1H\r\nEND:VEVENT\r\n");
        var o = Assert.Single(Ics.Expand(ics, From, To));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero), o.Start.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(1), o.End - o.Start);
    }

    [Fact]
    public void FoldedLinesAndEscapes()
    {
        var ics = Wrap("BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:Long\r\n  title\\, here\r\nDTSTART:20261103T090000Z\r\nDTEND:20261103T100000Z\r\nEND:VEVENT\r\n");
        Assert.Equal("Long title, here", Assert.Single(Ics.Expand(ics, From, To)).Title);
    }

    [Fact]
    public void WeeklyByDayOldSeriesWithExdateAndOverride()
    {
        // Every Monday and Wednesday since 2020; this week's Monday excluded,
        // this week's Wednesday moved to Thursday.
        var ics = Wrap(
            "BEGIN:VEVENT\r\nUID:s\r\nSUMMARY:Sync\r\nDTSTART:20200106T100000Z\r\nDTEND:20200106T103000Z\r\n" +
            "RRULE:FREQ=WEEKLY;BYDAY=MO,WE\r\nEXDATE:20261102T100000Z\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:s\r\nSUMMARY:Sync (moved)\r\nRECURRENCE-ID:20261104T100000Z\r\n" +
            "DTSTART:20261105T140000Z\r\nDTEND:20261105T143000Z\r\nEND:VEVENT\r\n");
        var list = Ics.Expand(ics, From, To.AddDays(1));
        Assert.Equal(new[] { "Sync (moved)", "Sync" }, list.Select(o => o.Title));
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 14, 0, 0, TimeSpan.Zero), list[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero), list[1].Start);
    }

    [Fact]
    public void CountAndUntilStopTheSeries()
    {
        var ics = Wrap(
            "BEGIN:VEVENT\r\nUID:c\r\nSUMMARY:C\r\nDTSTART:20261101T080000Z\r\nDTEND:20261101T090000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:u\r\nSUMMARY:U\r\nDTSTART:20261101T080000Z\r\nDTEND:20261101T090000Z\r\nRRULE:FREQ=DAILY;UNTIL=20261104T080000Z\r\nEND:VEVENT\r\n");
        var list = Ics.Expand(ics, From, To);
        Assert.Equal(2, list.Count(o => o.Title == "C"));   // 1st (before window), 2nd, 3rd
        Assert.Equal(3, list.Count(o => o.Title == "U"));   // 2nd, 3rd, 4th
    }

    [Fact]
    public void MonthlyNthWeekday()
    {
        // The first Tuesday of every month: 3 November 2026.
        var ics = Wrap("BEGIN:VEVENT\r\nUID:m\r\nSUMMARY:M\r\nDTSTART:20250107T120000Z\r\nDTEND:20250107T130000Z\r\nRRULE:FREQ=MONTHLY;BYDAY=1TU\r\nEND:VEVENT\r\n");
        var o = Assert.Single(Ics.Expand(ics, From, To));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), o.Start);
    }

    [Fact]
    public void CancelledAndAllDay()
    {
        var ics = Wrap(
            "BEGIN:VEVENT\r\nUID:x\r\nSUMMARY:Gone\r\nSTATUS:CANCELLED\r\nDTSTART:20261103T090000Z\r\nDTEND:20261103T100000Z\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:d\r\nSUMMARY:Holiday\r\nDTSTART;VALUE=DATE:20261104\r\nDTEND;VALUE=DATE:20261105\r\nEND:VEVENT\r\n");
        var o = Assert.Single(Ics.Expand(ics, From, To));
        Assert.True(o.AllDay);
    }

    [Fact]
    public void AlarmPropertiesDoNotLeakIntoTheEvent()
    {
        var ics = Wrap("BEGIN:VEVENT\r\nUID:a\r\nSUMMARY:Real\r\nDTSTART:20261103T090000Z\r\nDTEND:20261103T100000Z\r\n" +
                       "BEGIN:VALARM\r\nDESCRIPTION:Reminder\r\nTRIGGER:-PT10M\r\nEND:VALARM\r\nEND:VEVENT\r\n");
        var o = Assert.Single(Ics.Expand(ics, From, To));
        Assert.DoesNotContain("Reminder", o.Haystacks);
    }

    [Fact]
    public void DurationParsing()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), Ics.ParseDuration("PT1H30M"));
        Assert.Equal(TimeSpan.FromDays(8), Ics.ParseDuration("P1W1D"));
        Assert.Equal(TimeSpan.FromMinutes(-15), Ics.ParseDuration("-PT15M"));
    }
}

public class MeetingLinkTests
{
    [Fact]
    public void FindsKnownHostsOnly()
    {
        Assert.Equal("Google Meet", MeetingLink.Provider(MeetingLink.Find(new[] { "Join: https://meet.google.com/abc-defg-hij." })!));
        Assert.Equal("Zoom", MeetingLink.Provider(MeetingLink.Find(new[] { "<a href=\"https://us02web.zoom.us/j/123?pwd=x\">" })!));
        Assert.Null(MeetingLink.Find(new[] { "https://evil.example/zoom.us", "http://meet.google.com/abc" }));
    }
}

public class SnippetStoreTests
{
    static string TempFile() => Path.Combine(Path.GetTempPath(), "cyclop-" + Guid.NewGuid() + ".json");

    [Fact]
    public void LabelIsOptionalBothWays()
    {
        var path = TempFile();
        File.WriteAllText(path, "[{\"text\":\"+7 700\"},{\"label\":\"Mail\",\"text\":\"a@b.c\"}]");
        var store = new SnippetStore(path);
        store.Reload();
        Assert.False(store.FileBroken);
        Assert.Equal(2, store.Items.Count);
        store.Add("", "plain");
        var written = File.ReadAllText(path);
        Assert.Contains("\"text\": \"plain\"", written);
        Assert.DoesNotContain("\"label\": \"\"", written);
        File.Delete(path);
    }

    [Fact]
    public void BrokenFileIsNeverOverwritten()
    {
        var path = TempFile();
        File.WriteAllText(path, "[{\"text\": \"a\",}, oops");
        var store = new SnippetStore(path);
        store.Reload();
        Assert.True(store.FileBroken);
        store.Add("x", "y");
        Assert.Equal("[{\"text\": \"a\",}, oops", File.ReadAllText(path));
        File.Delete(path);
    }

    [Fact]
    public void SameNameDifferentValueKept_UpdateKeepsPosition()
    {
        var path = TempFile();
        var store = new SnippetStore(path);
        store.Add("pw", "one");
        store.Add("pw", "two");
        store.Add("other", "three");
        Assert.Equal(3, store.Items.Count);
        store.Update(store.Items[2], "pw2", "one!");
        Assert.Equal(new Snippet("pw2", "one!"), store.Items[2]);
        store.Move(store.Items[2], 0);
        Assert.Equal("pw2", store.Items[0].Label);
        Assert.Equal(SnippetKind.Phone, new Snippet("", "+7 (700) 123-45-67").Kind);
        Assert.Equal(SnippetKind.Mail, new Snippet("", "me@example.com").Kind);
        File.Delete(path);
    }

    [Fact]
    public void FilterIsCaseAndAccentBlind()
    {
        var store = new SnippetStore(TempFile());
        store.Add("Почта", "x@y.z");
        store.Add("Café", "1");
        Assert.Single(store.Filtered("почта"));
        Assert.Single(store.Filtered("cafe"));
    }
}

public class ConverterTests
{
    [Theory]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1.234,5", 1234.5)]
    [InlineData("12,5", 12.5)]
    [InlineData("1.", 1.0)]
    public void ParsesBothSeparators(string text, double expected) =>
        Assert.Equal(expected, CurrencyStore.Parse(text)!.Value, 6);

    [Fact]
    public void RejectsNonsense()
    {
        Assert.Null(CurrencyStore.Parse(""));
        Assert.Null(CurrencyStore.Parse("abc"));
    }

    [Fact]
    public void FormatsWithFractionByMagnitude()
    {
        var en = new System.Globalization.CultureInfo("en-US");
        Assert.Equal("1,234.5", CurrencyStore.Format(1234.5, en));
        Assert.Equal("0.0123", CurrencyStore.Format(0.0123, en));
        Assert.Equal("12", CurrencyStore.Format(12, en));
    }
}

public class TranslatorTests
{
    [Fact]
    public void RouteByScript()
    {
        Assert.Equal(new Translator.Route("ru", "en"), Translator.RouteFor("привет"));
        Assert.Equal(new Translator.Route("en", "ru"), Translator.RouteFor("hello"));
    }

    [Fact]
    public void ParsesResponseAndEntities()
    {
        Assert.Equal("it's", Translator.ParseResponse("{\"responseData\":{\"translatedText\":\"it&#39;s\"},\"responseStatus\":200}"));
        Assert.Throws<InvalidOperationException>(() =>
            Translator.ParseResponse("{\"responseData\":{\"translatedText\":\"\"},\"responseStatus\":\"403\",\"responseDetails\":\"quota\"}"));
    }

    [Fact]
    public void MyMemoryPicksTheBestRatedCandidateNotTheFirst()
    {
        // The answer that started this: a joke stored in the memory as a 92 % match.
        var json = "{\"responseData\":{\"translatedText\":\"май лайф май рулес\",\"match\":0.92},\"responseStatus\":200," +
                   "\"matches\":[{\"translation\":\"май лайф май рулес\",\"quality\":\"63\",\"match\":0.92}," +
                   "{\"translation\":\"Hi, how are you?\",\"quality\":\"74\",\"match\":0.89}]}";
        Assert.Equal("Hi, how are you?", Translator.ParseResponse(json));
    }

    [Fact]
    public void MyMemoryIgnoresWellRatedTranslationsOfOtherSentences()
    {
        // Real answer for "Let's meet tomorrow at ten and plan the next video."
        var json = "{\"responseData\":{\"translatedText\":\"Давайте встретимся завтра в десять и спланируем следующий ролик.\",\"match\":0.85},\"responseStatus\":200," +
                   "\"matches\":[{\"translation\":\"Давайте встретимся завтра в десять и спланируем следующий ролик.\",\"quality\":70,\"match\":0.85}," +
                   "{\"translation\":\"Важно оценивать результаты работы над ASO.\",\"quality\":\"74\",\"match\":0.21}]}";
        Assert.Equal("Давайте встретимся завтра в десять и спланируем следующий ролик.", Translator.ParseResponse(json));
    }

    [Fact]
    public void ParsesGoogleSentences()
    {
        var json = "[[[\"hi how are you doing?\\n\",\"привет как твои дела?\\n\",null],[\"second line\",\"вторая строка\",null]],null,\"ru\"]";
        Assert.Equal("hi how are you doing?\nsecond line", Translator.ParseGoogle(json));
    }

    [Fact]
    public void ChunksStayUnderTheLimit()
    {
        var text = string.Join(" ", Enumerable.Repeat("слово", 200));
        Assert.All(Translator.Chunks(text, 450), c => Assert.True(System.Text.Encoding.UTF8.GetByteCount(c) <= 450));
    }
}

public class L10nTests
{
    [Fact]
    public void ConvertsPrintfTemplates()
    {
        Assert.Equal("in {0} h {1} min", L10n.ToNetFormat("in %d h %d min"));
        Assert.Equal("{1} → {0}", L10n.ToNetFormat("%2$@ → %1$@"));
        Assert.Equal("{{x}} {0}", L10n.ToNetFormat("{x} %@"));
    }

    [Fact]
    public void RussianTableParsesAndCoversTheFormatKeys()
    {
        var asm = typeof(L10n).Assembly;
        using var stream = asm.GetManifestResourceStream("Cyclop.Strings.ru.strings")!;
        var table = L10n.Parse(new StreamReader(stream).ReadToEnd());
        Assert.Equal("Музыка", table["Music"]);
        Assert.Equal("через %d ч %d мин", table["in %d h %d min"]);
        Assert.Contains('\n', table["Cyclop needs access to Calendar. It is the only permission\nthe app asks for, and only for this tab."]);
    }
}

public class UpdateTests
{
    const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("0.9.1", "0.9.0", true)]
    [InlineData("0.10.0", "0.9.3", true)]
    [InlineData("win-v1.0", "0.9.9", true)]
    [InlineData("0.9.0", "0.9.0", false)]
    [InlineData("0.8.9", "0.9.0", false)]
    public void ComparesVersionsAsNumbers(string candidate, string current, bool newer) =>
        Assert.Equal(newer, Updates.IsNewer(candidate, current));

    [Fact]
    public void ParsesOnlyTrustedAnswers()
    {
        var good = Updates.Parse($"{{\"version\":\"0.9.1\",\"url\":\"https://github.com/a/b/releases/download/win-v0.9.1/Cyclop-win-x64.exe\",\"sha256\":\"{Sha}\"}}");
        Assert.Equal("0.9.1", good!.Version);
        Assert.Null(Updates.Parse($"{{\"version\":\"0.9.1\",\"url\":\"https://evil.example/Cyclop.exe\",\"sha256\":\"{Sha}\"}}"));
        Assert.Null(Updates.Parse($"{{\"version\":\"0.9.1\",\"url\":\"http://github.com/x.exe\",\"sha256\":\"{Sha}\"}}"));
        Assert.Null(Updates.Parse("{\"version\":\"0.9.1\",\"url\":\"https://github.com/x.exe\",\"sha256\":\"nothex\"}"));
        Assert.Null(Updates.Parse("{\"version\":null}"));
    }

    [Fact]
    public async Task HashMatchesKnownValue()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await Updates.HashAsync(path));
        File.Delete(path);
    }
}
