using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Cyclop;

/// <summary>
/// Looks a string up in the app's own table. The keys are the English text —
/// the same scheme as the macOS <c>Localizable.strings</c>, whose Russian table
/// this one starts from — so a missing entry degrades to English rather than
/// to a bare identifier.
/// </summary>
public static class L10n
{
    /// <summary>"ru" when Windows is set to Russian, "en" otherwise.</summary>
    public static string Language { get; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";

    /// <summary>
    /// Everything the app formats for itself — numbers, weekdays — follows the
    /// language the panel is in, not the system's: the two can differ, and a
    /// panel half in one language and half in another reads as a mistake.
    /// </summary>
    public static CultureInfo Culture { get; } = new(Language == "ru" ? "ru-RU" : "en-US");

    /// <summary>Lazy, so the parsing regex below exists by the time it runs.</summary>
    static readonly Lazy<Dictionary<string, string>> Table = new(() => Language == "ru" ? LoadRussian() : new());

    public static string T(string key) => Table.Value.TryGetValue(key, out var value) ? value : key;

    /// <summary>The same, with values filled in. Templates keep the printf
    /// spelling of the Mac tables (<c>%d</c>, <c>%@</c>), converted here.</summary>
    public static string T(string key, params object[] args) =>
        string.Format(Culture, ToNetFormat(T(key)), args);

    static Dictionary<string, string> LoadRussian()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Cyclop.Strings.ru.strings");
        if (stream is null) return new();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Parse(reader.ReadToEnd());
    }

    static readonly Regex Entry = new(
        "^\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*=\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*;",
        RegexOptions.Multiline);

    /// <summary>Reads the <c>"key" = "value";</c> format of a .strings file.</summary>
    internal static Dictionary<string, string> Parse(string text)
    {
        var table = new Dictionary<string, string>();
        foreach (Match match in Entry.Matches(text))
            table[Unescape(match.Groups[1].Value)] = Unescape(match.Groups[2].Value);
        return table;
    }

    static string Unescape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                i++;
                sb.Append(value[i] switch { 'n' => '\n', 't' => '\t', _ => value[i] });
            }
            else sb.Append(value[i]);
        }
        return sb.ToString();
    }

    static readonly Regex Placeholder = new(@"%(?:(\d+)\$)?[@ds]");

    /// <summary><c>"in %d h %d min"</c> → <c>"in {0} h {1} min"</c>; positional
    /// <c>%2$@</c> becomes <c>{1}</c>. Braces in the text are escaped first.</summary>
    internal static string ToNetFormat(string printf)
    {
        var escaped = printf.Replace("{", "{{").Replace("}", "}}");
        int next = 0;
        return Placeholder.Replace(escaped, m =>
        {
            int index = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) - 1 : next++;
            return "{" + index + "}";
        });
    }

    /// <summary>First character upper-cased, the rest left alone — Russian
    /// weekdays come out of a formatter lower-case, English ones do not, and the
    /// capital belongs to the position in the label, not to the word.</summary>
    public static string SentenceCased(this string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], Culture) + text[1..];
}
