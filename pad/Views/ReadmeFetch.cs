using System.Net.Http;

namespace Pad.Views;

/// <summary>
/// 从 GitHub 拉仓库 README（中文优先 README.zh.md，英文 README.md）。
/// raw.githubusercontent.com 在部分网络下会被卡住，所以给整次尝试一个硬预算（4 秒），
/// 并且准备一个 jsDelivr 的镜像候选；拿不到就快速失败，绝不让界面一直转圈。
/// </summary>
public static class ReadmeFetch
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    static readonly Dictionary<string, (string? Text, DateTime At)> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(2);

    static readonly string[] Zh =
    [
        "README.zh.md", "README_zh.md", "README.zh-CN.md", "README.zh-cn.md",
        "README-CN.md", "README.cn.md",
    ];

    static readonly string[] En =
    [
        "README.md", "readme.md", "README.markdown",
    ];

    /// <summary>拿不到返回 null（仓库没 README / 网络不通 / 非 GitHub 仓库）。</summary>
    public static async Task<string?> TryAsync(string repo, string lang, CancellationToken ct = default)
    {
        var key = repo + "|" + lang;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var hit) &&
                (hit.Text is not null || DateTime.UtcNow - hit.At < NegativeTtl))
                return hit.Text;
        }

        var names = lang == "zh" ? Zh : En;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(4));

        foreach (var name in names)
        {
            foreach (var url in new[]
                     {
                         $"https://raw.githubusercontent.com/{repo}/HEAD/{name}",
                         $"https://cdn.jsdelivr.net/gh/{repo}@HEAD/{name}",
                     })
            {
                try
                {
                    using var response = await Http.GetAsync(url, budget.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;
                    var text = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lock (Cache) Cache[key] = (text, DateTime.UtcNow);
                        return text;
                    }
                }
                catch (OperationCanceledException)
                {
                    // 总预算用完：直接收工
                    lock (Cache) Cache[key] = (null, DateTime.UtcNow);
                    return null;
                }
                catch
                {
                    // 这个候选拿不到，试下一个
                }
            }
        }

        lock (Cache) Cache[key] = (null, DateTime.UtcNow);
        return null;
    }

    /// <summary>用户点“重试”时清掉失败缓存。</summary>
    public static void Forget(string repo, string lang)
    {
        lock (Cache) Cache.Remove(repo + "|" + lang);
    }
}
