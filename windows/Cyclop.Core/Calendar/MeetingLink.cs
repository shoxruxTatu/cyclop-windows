using System.Text.RegularExpressions;

namespace Cyclop.Calendar;

/// <summary>
/// Finds the video call in an event. Only links to the known hosts are ever
/// returned, and only over https: an event is not the user's own text —
/// anyone who knows the address can put one in the calendar — and whatever it
/// carries would otherwise arrive as a button that says "Join".
/// </summary>
public static class MeetingLink
{
    static readonly (string Host, string Provider)[] Hosts =
    {
        ("meet.google.com", "Google Meet"),
        ("zoom.us", "Zoom"),
        ("teams.microsoft.com", "Teams"),
        ("teams.live.com", "Teams"),
        ("webex.com", "Webex"),
        ("whereby.com", "Whereby"),
        ("meet.jit.si", "Jitsi"),
        ("discord.gg", "Discord"),
        ("telemost.yandex.ru", "Телемост"),
    };

    static readonly Regex Link = new(@"https://[^\s<>""'\)\]\}]+", RegexOptions.IgnoreCase);

    public static Uri? Find(IEnumerable<string> haystacks)
    {
        foreach (var text in haystacks)
        {
            foreach (Match m in Link.Matches(text))
            {
                var candidate = m.Value.TrimEnd('.', ',', ';', ':', '!', '?');
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var url) && IsJoinable(url)) return url;
            }
        }
        return null;
    }

    /// <summary>Everything the join button is allowed to open.</summary>
    public static bool IsJoinable(Uri url) =>
        url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && Provider(url) is not null;

    public static string? Provider(Uri url)
    {
        var host = url.Host.ToLowerInvariant();
        foreach (var (known, provider) in Hosts)
            if (host == known || host.EndsWith("." + known)) return provider;
        return null;
    }
}
