using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cyclop;

/// <summary>What the update server says is current.</summary>
public sealed record UpdateInfo(string Version, string Url, string Sha256, string? Notes);

/// <summary>
/// Asks the update server for the newest build and downloads it. The server is
/// the author's Cloudflare Worker (see <c>windows/server</c>); it reads the
/// newest GitHub release, and the same request is what users are counted by.
///
/// A download is kept only when its SHA-256 matches what the release
/// published, and only from github.com over https — the file is about to
/// replace the program.
/// </summary>
public static class Updates
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    static Updates()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Cyclop-Windows");
    }

    public static async Task<UpdateInfo?> CheckAsync(string server, string version, string arch, string os,
        string? installId, CancellationToken token = default)
    {
        var url = $"{server.TrimEnd('/')}/v1/check?v={Uri.EscapeDataString(version)}&arch={Uri.EscapeDataString(arch)}" +
                  $"&os={Uri.EscapeDataString(os)}" + (installId is null ? "" : $"&id={Uri.EscapeDataString(installId)}");
        using var response = await Http.GetAsync(url, token);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(token));
    }

    /// <summary><c>{"version":"0.9.1","url":"https://github.com/…","sha256":"…"}</c>;
    /// a null version means nothing is published yet.</summary>
    public static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var version = Text("version");
        var url = Text("url");
        var sha = Text("sha256");
        if (version is null || url is null || sha is null) return null;
        if (!IsTrustedUrl(url) || sha.Length != 64 || !sha.All(Uri.IsHexDigit)) return null;
        return new UpdateInfo(version, url, sha.ToLowerInvariant(), Text("notes"));
    }

    /// <summary>Release assets live on github.com (and redirect to its storage).</summary>
    public static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host == "github.com" || u.Host.EndsWith(".github.com"));

    /// <summary>"0.10.0" is newer than "0.9.3": compared as numbers, not text.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Normalize(candidate), out var a) && Version.TryParse(Normalize(current), out var b) && a > b;

    static string Normalize(string v)
    {
        var s = v.Trim().TrimStart('v', 'V');
        if (s.StartsWith("win-v", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash > 0) s = s[..dash];
        return s.Count(c => c == '.') == 0 ? s + ".0" : s;
    }

    /// <summary>Downloads to <paramref name="folder"/> and returns the file —
    /// or throws when the bytes are not the ones the release published.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, string folder, CancellationToken token = default)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"Cyclop-{info.Version}.exe");
        var partial = path + ".part";
        using (var response = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, token))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using var output = File.Create(partial);
            await input.CopyToAsync(output, token);
        }
        var actual = await HashAsync(partial, token);
        if (actual != info.Sha256)
        {
            File.Delete(partial);
            throw new InvalidDataException($"checksum mismatch: {actual} != {info.Sha256}");
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }

    public static async Task<string> HashAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, token);
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }
}
