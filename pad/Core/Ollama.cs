using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pad.Core;

/// <summary>本机 Ollama 服务：探测、列模型、拉模型（一键装本地模型用）。</summary>
public static class Ollama
{
    public const string DefaultBase = "http://127.0.0.1:11434";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    public sealed record Model(string Name, long Size, DateTimeOffset? Modified)
    {
        public string SizeText => Size <= 0 ? ""
            : Size >= 1L << 30 ? $"{Size / (double)(1L << 30):0.#} GB"
            : $"{Size / (double)(1L << 20):0} MB";
    }

    /// <summary>ollama.com 上的一个模型（名字 + 一句话简介），用来给人选，不写死在代码里。</summary>
    public sealed record LibraryModel(string Name, string Summary)
    {
        public string Line => Summary.Length > 0 ? $"{Name}    {Summary}" : Name;
    }

    /// <summary>
    /// 拉 ollama.com/library 的模型名单（按站点的热门顺序）。
    /// 站点改版时返回空表——调用方要能退回「只用已装模型」。
    /// </summary>
    public static async Task<IReadOnlyList<LibraryModel>> LibraryAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var html = await http.GetStringAsync("https://ollama.com/library", ct);
        var list = new List<LibraryModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match card in Regex.Matches(html,
                     "<a href=\"/library/(?<name>[A-Za-z0-9._-]+)\" class=\"group w-full space-y-5\">(?<body>[\\s\\S]*?)</a>"))
        {
            var name = card.Groups["name"].Value;
            if (!seen.Add(name)) continue;
            var desc = Regex.Match(card.Groups["body"].Value, "<p[^>]*>(?<p>[\\s\\S]*?)</p>");
            var summary = WebUtility.HtmlDecode(Regex.Replace(desc.Groups["p"].Value, "<[^>]+>", "")).Trim();
            if (summary.Length > 70) summary = summary[..70].TrimEnd() + "…";
            list.Add(new LibraryModel(name, summary));
            if (list.Count >= 120) break;
        }
        return list;
    }

    /// <summary>某个模型的可用版本（latest / 7b / 1.5b …），按 tags 页出现顺序。</summary>
    public static async Task<IReadOnlyList<string>> TagsAsync(string model, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        var html = await http.GetStringAsync(
            "https://ollama.com/library/" + Uri.EscapeDataString(model) + "/tags", ct);
        var tags = new List<string>();
        foreach (Match m in Regex.Matches(html, "/library/" + Regex.Escape(model) + ":(?<tag>[A-Za-z0-9._-]+)"))
        {
            var tag = m.Groups["tag"].Value;
            // 量化变体（q4_0、q2_）和 -instruct 变体太多，只留 latest 和参数尺寸这几档。
            if (tag != "latest" && !Regex.IsMatch(tag, @"^\d+(\.\d+)?b$")) continue;
            if (!tags.Contains(tag)) tags.Add(tag);
        }
        return tags;
    }

    public sealed record Probe(bool Up, string Version, IReadOnlyList<Model> Models, string Error)
    {
        public static Probe Down(string why) => new(false, "", [], why);
    }

    public static async Task<Probe> ProbeAsync(string? baseUrl, CancellationToken ct)
    {
        var root = Normalize(baseUrl);
        try
        {
            string version = "";
            try { version = (await Http.GetStringAsync(root + "/api/version", ct)).Trim(); }
            catch { /* 老版本没有 /api/version，仍然算活着 */ }

            var json = await Http.GetStringAsync(root + "/api/tags", ct);
            using var doc = JsonDocument.Parse(json);
            var list = new List<Model>();
            if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in arr.EnumerateArray())
                {
                    var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Length == 0) continue;
                    var size = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                    DateTimeOffset? at = null;
                    if (m.TryGetProperty("modified_at", out var mo) && mo.TryGetDateTimeOffset(out var when)) at = when;
                    list.Add(new Model(name, size, at));
                }
            }
            return new Probe(true, Unquote(version, "version"), list.OrderBy(m => m.Name).ToList(), "");
        }
        catch (Exception ex)
        {
            return Probe.Down(Friendly(ex));
        }
    }

    /// <summary>
    /// 拉模型。Ollama 用 NDJSON 一行一步地推进度；这里把「下载/校验/写入」的
    /// 状态和字节进度翻成一行给人看的字，交给 UI 的进度条。
    /// </summary>
    public static async Task PullAsync(string? baseUrl, string model,
        IProgress<(string Status, long Done, long Total)> progress, CancellationToken ct)
    {
        var root = Normalize(baseUrl);
        var body = JsonSerializer.Serialize(new { name = model, stream = true });
        using var req = new HttpRequestMessage(HttpMethod.Post, root + "/api/pull")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw new PadError("PA153", "ollama refused to pull the model",
                model, text.Trim(), ["先确认 ollama 服务在跑：ollama serve"]);
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (TryParseProgress(line, out var status, out var done, out var total, out var error))
            {
                if (error is { Length: > 0 }) throw new PadError("PA154", "ollama pull failed", model, error, []);
                progress.Report((status, done, total));
            }
        }
    }

    /// <summary>一行 NDJSON → 状态/进度。抽出来单独测（网络不可测，解析可以）。</summary>
    public static bool TryParseProgress(string line, out string status, out long done, out long total, out string? error)
    {
        status = "";
        done = 0;
        total = 0;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                error = e.GetString() ?? "unknown";
                return true;
            }
            status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            if (root.TryGetProperty("completed", out var c) && c.TryGetInt64(out var cv)) done = cv;
            if (root.TryGetProperty("total", out var t) && t.TryGetInt64(out var tv)) total = tv;
            return status.Length > 0 || total > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string ProgressText(string status, long done, long total)
    {
        var label = status switch
        {
            "pulling manifest" => "读取清单",
            "verifying sha256 digest" => "校验文件",
            "writing manifest" => "写入清单",
            "removing any unused layers" => "清理旧层",
            "success" => "完成",
            "" => "准备中",
            _ => status,
        };
        if (total <= 0) return label;
        var pct = (int)Math.Clamp(done * 100 / total, 0, 100);
        return $"{label} · {pct}% · {Size(done)} / {Size(total)}";
    }

    /// <summary>本机装的 ollama.exe 在哪（每用户安装目录 → PATH）。</summary>
    public static string? FindExe()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe");
        if (File.Exists(local)) return local;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ollama.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* PATH 里有坏路径 */ }
        }
        return null;
    }

    /// <summary>后台起 <c>ollama serve</c>（装了但没启动时用）。</summary>
    public static bool TryStartServe(out string error)
    {
        error = "";
        var exe = FindExe();
        if (exe is null) { error = "这台机器没装 Ollama：去 ollama.com 下一个，装完再回来"; return false; }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            error = "启动失败：" + ex.Message;
            return false;
        }
    }

    public static string Normalize(string? baseUrl)    {
        var url = (baseUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0) url = DefaultBase;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        return url;
    }

    static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.#} GB"
        : $"{bytes / (double)(1L << 20):0} MB";

    static string Unquote(string text, string field)
    {
        // /api/version 返回 {"version":"0.3.12"}；老版本返回纯文本。
        if (text.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty(field, out var v)) return v.GetString() ?? "";
            }
            catch { }
        }
        return text;
    }

    static string Friendly(Exception ex) => ex switch
    {
        HttpRequestException => "没连上 Ollama（装了但没启动？先跑 ollama serve）",
        TaskCanceledException => "Ollama 没响应（超过 4 秒）",
        _ => ex.Message,
    };
}
