using System.Net;
using System.Text;
using System.Text.Json;

namespace Cyclop;

/// <summary>
/// English ↔ Russian, decided by script rather than by language detection: a
/// single word is far too short to identify, and "привет" comes back as
/// Bulgarian often enough to matter.
///
/// Windows has no on-device translator an app can call, so — unlike the Mac
/// version — this goes online: to Google Translate's public endpoint, and to
/// MyMemory when that does not answer. The pane says so under the field: text
/// typed here leaves the PC.
///
/// MyMemory is the fallback rather than the source because its first answer
/// is a translation memory lookup, and the memory is crowd-filled: "привет как
/// твои дела?" came back as "май лайф май рулес" — somebody's joke stored as a
/// 92 % match — with the real translation second in the list.
/// </summary>
public sealed class Translator : Observable
{
    /// <summary>MyMemory takes 500 bytes a request; Google takes far more by
    /// POST, but one request per a few thousand characters stays polite.</summary>
    const int ChunkLimit = 450;
    const int GoogleChunkLimit = 4500;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    CancellationTokenSource? pending;

    public string Input { get; private set; } = "";
    public string Output { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool IsBusy { get; private set; }

    public readonly record struct Route(string Source, string Target);

    public static Route RouteFor(string text) =>
        text.Any(c => c >= 'Ѐ' && c <= 'ӿ') ? new Route("ru", "en") : new Route("en", "ru");

    public Route CurrentRoute => RouteFor(Input.Trim());

    public static string Name(string code) => L10n.T(code == "ru" ? "Russian" : "English");

    /// <summary>Waits for the typing to pause, then asks. A newer keystroke
    /// cancels the older request, so answers never arrive out of order.</summary>
    public void SetInput(string text)
    {
        if (text == Input) return;
        Input = text;
        _ = RunAsync(delay: 450);
    }

    public void Retry() => _ = RunAsync(delay: 0);

    public void Reset()
    {
        pending?.Cancel();
        Input = "";
        Output = "";
        Failure = null;
        IsBusy = false;
        Notify();
    }

    async Task RunAsync(int delay)
    {
        pending?.Cancel();
        var cts = new CancellationTokenSource();
        pending = cts;
        var text = Input.Trim();
        if (text.Length == 0)
        {
            Output = "";
            Failure = null;
            IsBusy = false;
            Notify();
            return;
        }
        try
        {
            await Task.Delay(delay, cts.Token);
            IsBusy = true;
            Notify();
            var route = RouteFor(text);
            var result = await TranslateAsync(text, route, cts.Token);
            if (cts.IsCancellationRequested) return;
            Output = result;
            Failure = null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return; }
        catch (Exception e)
        {
            if (cts.IsCancellationRequested) return;
            Output = "";
            Failure = e is HttpRequestException or TaskCanceledException
                ? L10n.T("No connection to the translation service.")
                : e.Message;
        }
        IsBusy = false;
        Notify();
    }

    public static async Task<string> TranslateAsync(string text, Route route, CancellationToken token)
    {
        try { return await GoogleAsync(text, route, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) { Support.Log("Google Translate failed, falling back to MyMemory: " + e.Message); }
        return await MyMemoryAsync(text, route, token);
    }

    static async Task<string> GoogleAsync(string text, Route route, CancellationToken token)
    {
        var result = new StringBuilder();
        foreach (var chunk in Blocks(text, GoogleChunkLimit))
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&dt=t&sl=" + route.Source + "&tl=" + route.Target;
            using var body = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", chunk) });
            using var response = await Http.PostAsync(url, body, token);
            response.EnsureSuccessStatusCode();
            result.Append(ParseGoogle(await response.Content.ReadAsStringAsync(token)));
        }
        return result.ToString().Trim();
    }

    /// <summary><c>[[["translated","original",…],…],…]</c> — one entry per
    /// sentence, newlines kept inside them.</summary>
    internal static string ParseGoogle(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sentences = doc.RootElement[0];
        if (sentences.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("unexpected answer");
        var sb = new StringBuilder();
        foreach (var s in sentences.EnumerateArray())
            if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0 && s[0].ValueKind == JsonValueKind.String)
                sb.Append(s[0].GetString());
        return sb.ToString();
    }

    /// <summary>Whole lines grouped up to <paramref name="limit"/> characters.</summary>
    internal static IEnumerable<string> Blocks(string text, int limit)
    {
        var current = new StringBuilder();
        foreach (var line in Chunks(text, limit))
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > limit)
            {
                yield return current.ToString();
                current.Clear();
            }
            if (current.Length > 0) current.Append('\n');
            current.Append(line);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    static async Task<string> MyMemoryAsync(string text, Route route, CancellationToken token)
    {
        var parts = new List<string>();
        foreach (var chunk in Chunks(text, ChunkLimit))
        {
            if (string.IsNullOrWhiteSpace(chunk)) { parts.Add(chunk); continue; }
            var url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(chunk.Trim()) +
                      "&langpair=" + route.Source + "|" + route.Target;
            using var response = await Http.GetAsync(url, token);
            var json = await response.Content.ReadAsStringAsync(token);
            parts.Add(ParseResponse(json));
        }
        return string.Join("\n", parts);
    }

    /// <summary>The best of MyMemory's candidates, not the first: the first
    /// is the closest memory entry, whoever wrote it. Highest rated quality
    /// wins among the candidates for this very text; the headline answer only
    /// when nothing is rated.</summary>
    internal static string ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
        {
            string? best = null;
            double bestQuality = -1;
            foreach (var m in matches.EnumerateArray())
            {
                if (!m.TryGetProperty("translation", out var candidate) || candidate.GetString() is not { Length: > 0 } translation) continue;
                // "match" is how alike the stored source is to the text asked.
                // The memory also offers other people's sentences that merely
                // share a few words — "plan the next video" came back as a
                // well-rated line about ASO keywords, a 21 % match.
                if (Number(m, "match") is { } match && match < MinimumMatch) continue;
                double quality = Number(m, "quality") ?? 0;
                if (quality > bestQuality) { bestQuality = quality; best = translation; }
            }
            if (best is not null && bestQuality > 0) return WebUtility.HtmlDecode(best);
        }
        int status = root.TryGetProperty("responseStatus", out var s)
            ? s.ValueKind == JsonValueKind.Number ? s.GetInt32() : int.TryParse(s.GetString(), out var n) ? n : 0
            : 0;
        var translated = root.TryGetProperty("responseData", out var data) &&
                         data.TryGetProperty("translatedText", out var t) ? t.GetString() ?? "" : "";
        if (status != 200)
        {
            var details = root.TryGetProperty("responseDetails", out var d) ? d.GetString() : null;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? translated : details);
        }
        return WebUtility.HtmlDecode(translated);
    }

    /// <summary>MyMemory's own machine translation comes back as an 85 % match.</summary>
    const double MinimumMatch = 0.85;

    /// <summary>A number MyMemory sends either as a number or as a string.</summary>
    static double? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        return double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>The API takes 500 bytes a request. Lines travel whole when
    /// they fit; a longer one is cut between words.</summary>
    internal static IEnumerable<string> Chunks(string text, int limit)
    {
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (Encoding.UTF8.GetByteCount(paragraph) <= limit) { yield return paragraph; continue; }
            var current = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (current.Length > 0 && Encoding.UTF8.GetByteCount(current + " " + word) > limit)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
            if (current.Length > 0) yield return current.ToString();
        }
    }
}
