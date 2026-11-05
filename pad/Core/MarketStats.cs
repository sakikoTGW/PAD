using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Pad.Core;

/// <summary>
/// 商店卡片上的数字：GitHub 星数、npm 周下载、<c>engines.dsh</c> 兼容性。
/// 都带磁盘缓存（星数/下载 12h，engines 24h），失败就跳过——少一个数字不影响搜索。
/// </summary>
public static class MarketStats
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>GitHub 匿名额度用完后，本次会话不再打它。</summary>
    static DateTimeOffset _githubBlockedUntil;

    const int MaxRows = 48;

    static string Dir(Launcher launcher) => Path.Combine(launcher.Root, "library", "cache", "market", "stats");

    public static async Task EnrichAsync(Launcher launcher, IReadOnlyList<MarketRow> rows, string kernelVersion,
        IProgress<string>? log, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var dir = Dir(launcher);
        Directory.CreateDirectory(dir);

        using var gate = new SemaphoreSlim(4);
        var batch = rows.Take(MaxRows).ToList();
        await Task.WhenAll(batch.Select(async row =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (RepoSlug(row.Repository) is { } slug)
                    row.Stars = await StarCountAsync(dir, slug, ct);

                row.WeeklyDownloads = await WeeklyDownloadsAsync(dir, row.Name, ct);

                var engines = await EnginesDshAsync(dir, row.Name, ct);
                if (engines.Length > 0)
                {
                    row.EnginesDsh = engines;
                    if (kernelVersion.Length > 0)
                        row.Compatible = PackSemver.Satisfies(kernelVersion, engines);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // 一个包取不到不影响别的。
            }
            finally { gate.Release(); }
        }));
    }

    /// <summary>卡片上用的缩写：1234 → 1.2k，1234567 → 1.2m。</summary>
    public static string Compact(long value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000d).ToString("0.#") + "m",
        >= 1_000 => (value / 1_000d).ToString("0.#") + "k",
        _ => value.ToString(),
    };

    /// <summary><c>https://github.com/o/r.git</c> / <c>git+…</c> / <c>github:o/r</c> → <c>o/r</c>。</summary>
    public static string? RepoSlug(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return null;
        var text = repository.Trim();
        if (text.StartsWith("github:", StringComparison.OrdinalIgnoreCase)) return Clean(text[7..]);
        var at = text.IndexOf("github.com/", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) return Clean(text[(at + "github.com/".Length)..]);
        return null;

        static string? Clean(string tail)
        {
            var cut = tail.Split('#', '?')[0].Trim('/');
            if (cut.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) cut = cut[..^4];
            var parts = cut.Split('/');
            return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0 ? parts[0] + "/" + parts[1] : null;
        }
    }

    static async Task<long> StarCountAsync(string dir, string slug, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < _githubBlockedUntil) return 0;
        var cache = Path.Combine(dir, "stars-" + slug.Replace('/', '_') + ".json");
        if (ReadCache(cache, TimeSpan.FromHours(12)) is { } cached) return cached.GetProperty("stars").GetInt64();

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + slug);
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("User-Agent", "PAD");
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            _githubBlockedUntil = DateTimeOffset.UtcNow.AddMinutes(30);
            return 0;
        }
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var stars = doc.RootElement.TryGetProperty("stargazers_count", out var value) ? value.GetInt64() : 0;
        WriteCache(cache, new { stars });
        return stars;
    }

    static async Task<long> WeeklyDownloadsAsync(string dir, string name, CancellationToken ct)
    {
        var cache = Path.Combine(dir, "dl-" + Safe(name) + ".json");
        if (ReadCache(cache, TimeSpan.FromHours(12)) is { } cached) return cached.GetProperty("downloads").GetInt64();

        var url = "https://api.npmjs.org/downloads/point/last-week/" + Uri.EscapeDataString(name);
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var downloads = doc.RootElement.TryGetProperty("downloads", out var value) ? value.GetInt64() : 0;
        WriteCache(cache, new { downloads });
        return downloads;
    }

    static async Task<string> EnginesDshAsync(string dir, string name, CancellationToken ct)
    {
        var cache = Path.Combine(dir, "eng-" + Safe(name) + ".json");
        if (ReadCache(cache, TimeSpan.FromHours(24)) is { } cached)
            return cached.TryGetProperty("dsh", out var dsh) ? dsh.GetString() ?? "" : "";

        var url = "https://registry.npmjs.org/" + Uri.EscapeDataString(name).Replace("%2F", "%2f") + "/latest";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
        var range = doc.RootElement.TryGetProperty("engines", out var engines)
                    && engines.TryGetProperty("dsh", out var dshValue)
            ? dshValue.GetString() ?? "" : "";
        WriteCache(cache, new { dsh = range });
        return range;
    }

    static JsonElement? ReadCache(string path, TimeSpan ttl)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var at = doc.RootElement.TryGetProperty("at", out var stamp) ? stamp.GetDateTimeOffset() : DateTimeOffset.MinValue;
            if (DateTimeOffset.UtcNow - at > ttl) return null;
            return doc.RootElement.GetProperty("value").Clone();
        }
        catch { return null; }
    }

    static void WriteCache(string path, object value)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, value });
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch { /* 缓存写不进去不影响这次结果 */ }
    }

    static string Safe(string name) => name.Replace('/', '_').Replace('@', '_');

    /// <summary>精选（编辑推荐）：内置一小批，外加 library/market/featured.json 的追加项。</summary>
    static readonly HashSet<string> Featured = new(StringComparer.OrdinalIgnoreCase)
    {
        "@deepseek-ai/dsh-web-app",
        "@deepseek-ai/dsh-base",
        "@deepseek-harness-tui/dsh-tui",
        "dsh-better-sidebar",
        "dsh-cost-meter",
        "dsh-find-skill",
    };

    public static bool IsFeatured(string name) => Featured.Contains(name);
}
