using System.Text.RegularExpressions;
using Markdig;

namespace Pad.Views;

/// <summary>
/// README → 一份接近 GitHub 版式的 HTML（给 WebView2 用）。
/// Markdig 负责 CommonMark/GFM（表格、任务列表、自动链接、脚注…），
/// 这里只补两件事：GitHub 风格的 CSS，以及把 README 里的相对图片/链接改写到 raw/blob。
/// </summary>
public static class ReadmeHtml
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .Build();

    static readonly Regex SrcPattern = new("""src\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase);
    static readonly Regex HrefPattern = new("""href\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase);

    public static string Build(string markdown, string repo, string lang)
    {
        var body = Markdown.ToHtml(markdown, Pipeline);
        body = Rewrite(body, repo);
        var dir = lang == "zh" ? "zh" : "en";
        return $"""
<!doctype html>
<html lang="{dir}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
{Css}
</style>
</head>
<body>
<article class="markdown-body">
{body}
</article>
</body>
</html>
""";
    }

    static string Rewrite(string html, string repo)
    {
        html = SrcPattern.Replace(html, m =>
        {
            var url = m.Groups[1].Value.Trim();
            if (External(url)) return m.Value;
            return $"src=\"https://raw.githubusercontent.com/{repo}/HEAD/{url.TrimStart('.', '/')}\"";
        });
        html = HrefPattern.Replace(html, m =>
        {
            var url = m.Groups[1].Value.Trim();
            if (External(url)) return m.Value;
            var path = url.TrimStart('.', '/');
            if (path.Length == 0) return $"href=\"https://github.com/{repo}\"";
            return $"href=\"https://github.com/{repo}/blob/HEAD/{path}\"";
        });
        return html;
    }

    static bool External(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith('#');

    // GitHub 浅色版式的子集（字体 / 标题分隔线 / 代码块 / 表格 / 引用 / 徽章图片）。
    const string Css = """
    :root { color-scheme: light; }
    html, body { margin: 0; padding: 0; background: #ffffff; }
    body {
      font-family: -apple-system, "Segoe UI", "Microsoft YaHei UI", Helvetica, Arial, sans-serif;
      font-size: 15px;
      line-height: 1.6;
      color: #1f2328;
      -webkit-font-smoothing: antialiased;
    }
    .markdown-body { max-width: 760px; margin: 0 auto; padding: 22px 28px 40px; }
    .markdown-body > *:first-child { margin-top: 0 !important; }
    .markdown-body h1, .markdown-body h2, .markdown-body h3,
    .markdown-body h4, .markdown-body h5, .markdown-body h6 {
      margin: 26px 0 14px; font-weight: 600; line-height: 1.25;
    }
    .markdown-body h1 { font-size: 2em; padding-bottom: .3em; border-bottom: 1px solid #d1d9e0; }
    .markdown-body h2 { font-size: 1.5em; padding-bottom: .3em; border-bottom: 1px solid #d1d9e0; }
    .markdown-body h3 { font-size: 1.25em; }
    .markdown-body h4 { font-size: 1em; }
    .markdown-body h5 { font-size: .875em; }
    .markdown-body h6 { font-size: .85em; color: #59636e; }
    .markdown-body p, .markdown-body blockquote, .markdown-body ul,
    .markdown-body ol, .markdown-body dl, .markdown-body table, .markdown-body pre {
      margin-top: 0; margin-bottom: 16px;
    }
    .markdown-body a { color: #0969da; text-decoration: none; }
    .markdown-body a:hover { text-decoration: underline; }
    .markdown-body strong { font-weight: 600; }
    .markdown-body ul, .markdown-body ol { padding-left: 2em; }
    .markdown-body li + li { margin-top: .25em; }
    .markdown-body li > p { margin-top: 16px; }
    .markdown-body li input[type="checkbox"] { margin: 0 .4em .25em -1.4em; vertical-align: middle; }
    .markdown-body blockquote {
      padding: 0 1em; color: #59636e; border-left: .25em solid #d1d9e0; margin-left: 0;
    }
    .markdown-body code, .markdown-body tt {
      padding: .2em .4em; margin: 0; font-size: 85%;
      font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, "Liberation Mono", monospace;
      background: #eff1f3; border-radius: 6px;
    }
    .markdown-body pre {
      padding: 16px; overflow: auto; font-size: 85%; line-height: 1.45;
      background: #f6f8fa; border-radius: 6px;
    }
    .markdown-body pre code {
      padding: 0; margin: 0; background: transparent; border: 0; font-size: 100%;
      display: block; overflow: visible; word-wrap: normal; white-space: pre;
    }
    .markdown-body table { border-collapse: collapse; width: max-content; max-width: 100%; overflow: auto; display: block; }
    .markdown-body table th, .markdown-body table td { padding: 6px 13px; border: 1px solid #d1d9e0; }
    .markdown-body table tr:nth-child(2n) { background: #f6f8fa; }
    .markdown-body th { font-weight: 600; }
    .markdown-body img { max-width: 100%; box-sizing: content-box; }
    .markdown-body hr { height: .25em; padding: 0; margin: 24px 0; background: #d1d9e0; border: 0; }
    .markdown-body del { color: #59636e; }
    .markdown-body .task-list-item { list-style-type: none; }
    .markdown-body .task-list-item-checkbox { margin-right: .4em; }
    .markdown-body details { margin-bottom: 16px; }
    .markdown-body summary { cursor: pointer; font-weight: 600; }
    """;
}
