namespace Pad.Core;

/// <summary>
/// Store (npm market) use-cases shared by the download page — search a shelf and
/// install a package into an instance profile through <see cref="PluginCommands"/>.
/// </summary>
public static class MarketCommands
{
    public sealed record InstallResult(HotApplyResult Apply, bool IsBundle, string Spec);

    public sealed record SearchResult(MarketPage Page, bool Pinyin);

    /// <summary>Install one npm package into a profile (cold by default; same path as CLI).</summary>
    /// <param name="version">Pinned version for the install spec; empty means latest tag.</param>
    /// <param name="probeVersion">Version used only for the <c>dsh.bundle</c> registry probe
    /// (defaults to <paramref name="version"/>, then <c>latest</c>).</param>
    public static async Task<InstallResult> InstallAsync(
        Launcher launcher, Instance inst, string profile,
        string name, string version, IProgress<string>? log, CancellationToken ct,
        bool wantHot = false, string? probeVersion = null)
    {
        var ver = (version ?? "").Trim();
        var spec = ver.Length > 0 ? $"{name}@{ver}" : name;
        var probe = (probeVersion ?? ver).Trim();
        if (probe.Length == 0) probe = "latest";
        var layer = await Market.IsBundle(name, probe, ct);
        var apply = await PluginCommands.AddAsync(launcher, inst, profile, spec, log, ct, wantHot);
        return new InstallResult(apply, layer, spec);
    }

    /// <summary>
    /// One shelf page. On the plugin shelf, empty pinyin-looking queries fall back to the
    /// local pinyin index (same behaviour the download page used to inline).
    /// </summary>
    public static async Task<SearchResult> SearchShelfAsync(
        Launcher launcher, string shelfId, string shelfQuery, string text, int from, int size,
        IProgress<string>? status, CancellationToken ct)
    {
        var page = await Market.Search(shelfQuery, text, from, ct, size);
        if (ct.IsCancellationRequested) return new SearchResult(page, false);

        if (shelfId == "plugin" && text.Length > 0 && PinyinSearch.LooksLikePinyinQuery(text) &&
            !page.Rows.Any(r => Market.NameMatches(r, text)))
        {
            status?.Report("正在匹配拼音…");
            if (await MarketPinyinIndex.EnsureAsync(launcher, status, ct))
            {
                var hits = MarketPinyinIndex.Lookup(text);
                if (hits.Count > 0)
                    return new SearchResult(new MarketPage(hits, hits.Count, 0, hits.Count), true);
            }
        }

        return new SearchResult(page, false);
    }
}
