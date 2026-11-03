using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cyclop;

public sealed record Currency(string Code, string Name)
{
    public string DisplayCode => Code.ToUpperInvariant();
}

/// <summary>
/// Live exchange rates from fawazahmed0/exchange-api: the jsDelivr CDN first,
/// the Cloudflare Pages mirror the upstream asks every client to keep as a
/// fallback. Rates land once a day upstream; this refreshes hourly and again
/// whenever the source currency changes — the base the API answers against.
/// </summary>
public sealed class CurrencyStore : Observable
{
    enum Side { Source, Target }

    const string PrimaryHost = "https://cdn.jsdelivr.net/npm/@fawazahmed0/currency-api@latest/v1";
    const string FallbackHost = "https://latest.currency-api.pages.dev/v1";

    /// <summary>Currencies people actually reach for, pinned to the top.</summary>
    static readonly string[] Pinboard =
    {
        "usd", "eur", "rub", "uzs", "kzt", "gbp", "cny", "jpy", "chf", "try", "uah",
        "byn", "pln", "aed", "btc", "eth", "usdt",
    };

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // jsDelivr sends a week-long max-age on these files; a cached answer
        // would keep yesterday's rates no matter how often we "refresh".
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        client.DefaultRequestHeaders.Pragma.Add(new NameValueHeaderValue("no-cache"));
        return client;
    }

    readonly AppState state;
    Repeating? timer;
    Side editing = Side.Source;
    bool suppress;
    string sourceCode, targetCode;

    public List<Currency> Currencies { get; private set; } = new();
    public Dictionary<string, double> Rates { get; private set; } = new();
    public string RateBase { get; private set; } = "";
    public string? RateDate { get; private set; }
    public bool IsLoading { get; private set; }
    public string? Failure { get; private set; }
    public string SourceText { get; private set; } = "1";
    public string TargetText { get; private set; } = "";

    public CurrencyStore(AppState state)
    {
        this.state = state;
        sourceCode = state.CurrencySource?.ToLowerInvariant() ?? "usd";
        targetCode = state.CurrencyTarget?.ToLowerInvariant() ?? DefaultTarget();
        if (sourceCode == targetCode) targetCode = sourceCode == "usd" ? "eur" : "usd";
    }

    /// <summary>The PC's own currency, or RUB when the region has none worth
    /// converting against.</summary>
    static string DefaultTarget()
    {
        try
        {
            var code = RegionInfo.CurrentRegion.ISOCurrencySymbol.ToLowerInvariant();
            if (code.Length == 3 && code != "usd") return code;
        }
        catch { }
        return "rub";
    }

    public string SourceCode
    {
        get => sourceCode;
        set
        {
            value = value.ToLowerInvariant();
            if (value == sourceCode) return;
            sourceCode = value;
            PersistPair();
            _ = RefreshRatesAsync();
            if (!suppress) Recompute(Side.Source);
            Notify();
        }
    }

    public string TargetCode
    {
        get => targetCode;
        set
        {
            value = value.ToLowerInvariant();
            if (value == targetCode) return;
            targetCode = value;
            PersistPair();
            if (!suppress) Recompute(Side.Source);
            Notify();
        }
    }

    public double? Rate =>
        RateBase == sourceCode && Rates.TryGetValue(targetCode, out var rate) && rate > 0 ? rate : null;

    public void Start()
    {
        _ = BootstrapAsync();
        timer?.Dispose();
        timer = new Repeating(TimeSpan.FromHours(1), () => _ = RefreshRatesAsync());
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    /// <summary>Opening the tab always asks again: a table already in memory
    /// is what made yesterday's rates look permanent after a night asleep.</summary>
    public void RefreshIfNeeded() => _ = RefreshRatesAsync();

    public void SetSourceText(string text)
    {
        editing = Side.Source;
        SourceText = text;
        Recompute(Side.Source);
        Notify();
    }

    public void SetTargetText(string text)
    {
        editing = Side.Target;
        TargetText = text;
        Recompute(Side.Target);
        Notify();
    }

    public void Swap()
    {
        var previousSource = sourceCode;
        var typed = SourceText;
        suppress = true;
        SourceCode = targetCode;
        TargetCode = previousSource;
        suppress = false;
        SourceText = typed;
        editing = Side.Source;
        if (RateBase == sourceCode) Recompute(Side.Source);
        Notify();
    }

    public IEnumerable<Currency> Filtered(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return Currencies;
        return Currencies.Where(c => c.Code.Contains(q) || c.Name.ToLowerInvariant().Contains(q));
    }

    public string Name(string code) =>
        Currencies.FirstOrDefault(c => c.Code == code)?.Name ?? code.ToUpperInvariant();

    async Task BootstrapAsync()
    {
        IsLoading = true;
        Notify();
        try
        {
            await LoadCurrenciesAsync();
            await LoadRatesAsync(sourceCode);
            Failure = null;
            Recompute(editing);
        }
        catch (Exception e) { Failure = e.Message; }
        IsLoading = false;
        Notify();
    }

    async Task RefreshRatesAsync()
    {
        try
        {
            if (Currencies.Count == 0) await LoadCurrenciesAsync();
            await LoadRatesAsync(sourceCode);
            Failure = null;
            Recompute(editing);
        }
        catch (Exception e)
        {
            // Keep the rates we have; only an empty table is worth a message.
            if (Rates.Count == 0) Failure = e.Message;
        }
        Notify();
    }

    async Task LoadCurrenciesAsync()
    {
        var json = await FetchAsync("currencies.min.json");
        var decoded = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        Currencies = decoded
            .Select(p => new Currency(p.Key.ToLowerInvariant(), string.IsNullOrEmpty(p.Value) ? p.Key.ToUpperInvariant() : p.Value))
            .OrderBy(c => Array.IndexOf(Pinboard, c.Code) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .ToList();
        if (Currencies.All(c => c.Code != sourceCode)) sourceCode = Currencies.FirstOrDefault()?.Code ?? "usd";
        if (Currencies.All(c => c.Code != targetCode))
            targetCode = Currencies.FirstOrDefault(c => c.Code != sourceCode)?.Code ?? "eur";
    }

    async Task LoadRatesAsync(string baseCode)
    {
        var code = baseCode.ToLowerInvariant();
        var json = await FetchAsync($"currencies/{code}.min.json");
        using var doc = JsonDocument.Parse(json);
        var parsed = new Dictionary<string, double> { [code] = 1 };
        string? date = doc.RootElement.TryGetProperty("date", out var d) ? d.GetString() : null;
        if (doc.RootElement.TryGetProperty(code, out var table) && table.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in table.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) parsed[p.Name.ToLowerInvariant()] = p.Value.GetDouble();
        }
        // A newer base was picked while this one was in flight.
        if (code != sourceCode) return;
        Rates = parsed;
        RateBase = code;
        RateDate = date;
    }

    static async Task<string> FetchAsync(string endpoint)
    {
        Exception? last = null;
        foreach (var host in new[] { PrimaryHost, FallbackHost })
        {
            try
            {
                using var response = await Http.GetAsync($"{host}/{endpoint}");
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception e) { last = e; }
        }
        throw last ?? new HttpRequestException("no host answered");
    }

    void Recompute(Side side)
    {
        // Rates belong to a base; a stale table from the previous source must
        // not invent a conversion.
        if (Rate is not { } rate) return;
        if (side == Side.Source)
        {
            if (SourceText.Trim().Length == 0) { TargetText = ""; return; }
            if (Parse(SourceText) is { } amount) TargetText = Format(amount * rate);
        }
        else
        {
            if (TargetText.Trim().Length == 0) { SourceText = ""; return; }
            if (Parse(TargetText) is { } amount) SourceText = Format(amount / rate);
        }
    }

    void PersistPair()
    {
        state.CurrencySource = sourceCode;
        state.CurrencyTarget = targetCode;
        state.Save();
    }

    /// <summary>Accepts both "1 234,56" and "1,234.56": the rightmost
    /// separator is the decimal one. A mid-edit "1." still reads.</summary>
    public static double? Parse(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return null;
        s = s.Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace("'", "");
        if (s.Contains(',') && s.Contains('.'))
        {
            if (s.LastIndexOf(',') > s.LastIndexOf('.')) s = s.Replace(".", "").Replace(',', '.');
            else s = s.Replace(",", "");
        }
        else s = s.Replace(',', '.');
        if (s.EndsWith('.')) s = s[..^1];
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)
            ? v : null;
    }

    /// <summary>Fiat keeps a couple of decimals, tiny crypto rates keep more.</summary>
    public static string Format(double value, CultureInfo? culture = null)
    {
        if (!double.IsFinite(value)) return "";
        var abs = Math.Abs(value);
        int fraction = abs >= 1 ? 2 : abs >= 0.01 ? 4 : abs >= 0.0001 ? 6 : 8;
        return value.ToString("#,0." + new string('#', fraction), culture ?? L10n.Culture);
    }
}
