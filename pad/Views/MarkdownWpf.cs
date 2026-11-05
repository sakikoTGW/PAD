using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;

namespace Pad.Views;

/// <summary>
/// README 的 WPF 渲染：Markdig 解析（CommonMark + GFM 表格/任务列表/删除线/自动链接），
/// 再按 GitHub 浅色版式铺成 WPF 控件。纯 WPF 元素 → 能跟着窗口做滑入/淡入动画，也不受 WebView2 启动耗时影响。
/// </summary>
public static class MarkdownWpf
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    static readonly Regex ImgSrc = new("""<img[^>]+src\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase);
    static readonly Regex Tag = new("<[^>]+>", RegexOptions.Compiled);

    static Brush BrushOf(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush b) return b;
        }
        catch { /* 主题没加载时用兜底色 */ }
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
    }

    static double SizeOf(string key, double fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is double d) return d;
        }
        catch { }
        return fallback;
    }

    static FontFamily FontOf(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is FontFamily f) return f;
        }
        catch { }
        return new FontFamily(fallback);
    }

    static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Courier New");

    /// <summary>预热 Markdig 管道（首次 Parse 要编译正则/扩展，约 300ms）；开窗时后台跑一次。</summary>
    public static void Warmup()
    {
        try
        {
            // 走一遍完整渲染（JIT + 资源解析 + 表格/列表/代码块分支），不只是 Parse。
            var probe = new StackPanel();
            Render("# warmup\n\n- a\n\n```\nx\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n> q", probe, "warmup/warmup");
        }
        catch { /* 预热失败不影响真正渲染 */ }
    }
    /// <summary>
    /// 异步渲染：Markdig 解析放后台线程；建控件回 UI 线程、每批 12 个块并让出消息循环。
    /// 不管调用方在哪个线程，都不会卡界面，也不会把控件建到非 UI 线程。
    /// </summary>
    public static async Task RenderAsync(string markdown, Panel host, string repo)
    {
        var ui = host.Dispatcher;
        if (ui is null) return;

        ui.Invoke(() => host.Children.Clear());
        if (string.IsNullOrWhiteSpace(markdown))
        {
            ui.Invoke(() => host.Children.Add(Muted("这个仓库没有 README。")));
            return;
        }

        const int cap = 150_000;
        var truncated = markdown.Length > cap;
        var source = truncated ? markdown[..cap] : markdown;

        MarkdownDocument doc;
        try
        {
            doc = await Task.Run(() => Markdown.Parse(source, Pipeline)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await ui.InvokeAsync(() => host.Children.Add(Muted("README 解析失败：" + ex.Message)));
            return;
        }

        var ctx = new Ctx(repo);
        var blocks = doc.ToList();
        const int maxBlocks = 250;
        if (blocks.Count > maxBlocks)
        {
            blocks = blocks.Take(maxBlocks).ToList();
            truncated = true;
        }
        for (var i = 0; i < blocks.Count; i += 6)
        {
            var slice = blocks.Skip(i).Take(6).ToList();
            await ui.InvokeAsync(() =>
            {
                foreach (var block in slice) RenderBlock(block, host, ctx);
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        if (truncated)
            await ui.InvokeAsync(() => host.Children.Add(Muted("（README 太长，后面省略。）")));
    }
    public static void Render(string markdown, Panel host, string repo)
    {
        host.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            host.Children.Add(Muted("这个仓库没有 README。"));
            return;
        }

        MarkdownDocument doc;
        try { doc = Markdown.Parse(markdown, Pipeline); }
        catch (Exception ex)
        {
            host.Children.Add(Muted("README 解析失败：" + ex.Message));
            return;
        }

        var ctx = new Ctx(repo);
        foreach (var block in doc)
        {
            if (RenderBlock(block, host, ctx) == false) continue;
        }
    }

    sealed class Ctx(string repo)
    {
        public string Repo { get; } = repo;
    }

    static bool RenderBlock(MdBlock block, Panel host, Ctx ctx)
    {
        switch (block)
        {
            case HeadingBlock heading:
                host.Children.Add(Heading(heading));
                return true;
            case ParagraphBlock paragraph:
                host.Children.Add(Paragraph(paragraph.Inline, ctx));
                return true;
            case ListBlock list:
                host.Children.Add(List(list, ctx));
                return true;
            case QuoteBlock quote:
                host.Children.Add(Quote(quote, ctx));
                return true;
            case FencedCodeBlock fenced:
                host.Children.Add(CodeBlock(string.Join("\n", fenced.Lines.Lines.Select(l => l.ToString()))));
                return true;
            case CodeBlock code:
                host.Children.Add(CodeBlock(string.Join("\n", code.Lines.Lines.Select(l => l.ToString()))));
                return true;
            case ThematicBreakBlock:
                host.Children.Add(new Border
                {
                    Height = 1,
                    Background = BrushOf("LineDivider", "#E3E8EB"),
                    Margin = new Thickness(0, 14, 0, 14),
                });
                return true;
            case MdTable table:
                host.Children.Add(Table(table, ctx));
                return true;
            case HtmlBlock html:
                host.Children.Add(Html(html.Lines.ToString(), ctx));
                return true;
            case LinkReferenceDefinitionGroup:
                return true;
            default:
                if (block is LeafBlock leaf && leaf.Lines.Count > 0)
                {
                    var text = string.Join("\n", leaf.Lines.Lines.Select(l => l.ToString()));
                    if (!string.IsNullOrWhiteSpace(text)) host.Children.Add(Muted(text.Trim()));
                }
                return true;
        }
    }

    // ── 块级 ──────────────────────────────────────────────────────────

    static FrameworkElement Heading(HeadingBlock block)
    {
        var text = PlainInline(block.Inline);
        var size = block.Level switch { 1 => 26d, 2 => 21d, 3 => 17d, 4 => 15d, _ => 14d };
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushOf("TextPrimary", "#1F2328"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, block.Level <= 2 ? 22 : 14, 0, block.Level <= 2 ? 10 : 6),
        };
        if (block.Level > 2) return tb;
        return new Border
        {
            BorderBrush = BrushOf("LineDivider", "#D1D9E0"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = tb,
            Margin = new Thickness(0, 22, 0, 10),
            Padding = new Thickness(0, 0, 0, 6),
        };
    }

    static FrameworkElement Paragraph(ContainerInline? inline, Ctx ctx)
    {
        var tb = new TextBlock
        {
            FontSize = 14,
            LineHeight = 25,
            TextWrapping = TextWrapping.Wrap,
            Foreground = BrushOf("TextSecondary", "#1F2328"),
            Margin = new Thickness(0, 4, 0, 8),
        };
        AddInlines(tb.Inlines, inline, ctx);
        return tb;
    }

    static FrameworkElement List(ListBlock list, Ctx ctx)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 8) };
        var index = 0;
        foreach (var item in list)
        {
            if (item is not ListItemBlock li) continue;
            index++;
            var marker = list.IsOrdered ? index + "." : "•";
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bullet = new TextBlock
            {
                Text = marker,
                FontSize = 14,
                Foreground = BrushOf("TextMuted", "#59636E"),
                Margin = new Thickness(list.IsOrdered ? 2 : 4, 0, 0, 0),
            };
            var content = new StackPanel();
            foreach (var child in li)
            {
                if (child is ParagraphBlock p) content.Children.Add(Paragraph(p.Inline, ctx));
                else RenderBlock(child, content, ctx);
            }
            Grid.SetColumn(bullet, 0);
            Grid.SetColumn(content, 1);
            grid.Children.Add(bullet);
            grid.Children.Add(content);
            panel.Children.Add(grid);
        }
        return panel;
    }

    static FrameworkElement Quote(QuoteBlock quote, Ctx ctx)
    {
        var inner = new StackPanel();
        foreach (var child in quote) RenderBlock(child, inner, ctx);
        return new Border
        {
            BorderBrush = BrushOf("AccentSoft", "#D1D9E0"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(14, 2, 0, 2),
            Margin = new Thickness(0, 6, 0, 10),
            Child = inner,
        };
    }

    static FrameworkElement CodeBlock(string code) => new Border
    {
        Background = BrushOf("BgSubtle", "#F6F8FA"),
        BorderBrush = BrushOf("LineBorder", "#D1D9E0"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 10, 14, 10),
        Margin = new Thickness(0, 6, 0, 12),
        Child = new TextBlock
        {
            Text = code,
            FontFamily = Mono,
            FontSize = 12.5,
            Foreground = BrushOf("TextSecondary", "#1F2328"),
            TextWrapping = TextWrapping.NoWrap,
        },
    };

    static FrameworkElement Table(MdTable table, Ctx ctx)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 12) };
        var rows = table.OfType<MdTableRow>().ToList();
        if (rows.Count == 0) return grid;
        var cols = rows.Max(r => r.Count);
        for (var c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < rows.Count; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < row.Count; c++)
            {
                if (row[c] is not MdTableCell cell) continue;
                var cellPanel = new StackPanel();
                foreach (var child in cell)
                    if (child is ParagraphBlock p) cellPanel.Children.Add(Paragraph(p.Inline, ctx));
                var isHeader = r == 0;
                var border = new Border
                {
                    BorderBrush = BrushOf("LineBorder", "#D1D9E0"),
                    BorderThickness = new Thickness(1),
                    Background = r % 2 == 0 && !isHeader
                        ? BrushOf("BgSubtle", "#F6F8FA")
                        : Brushes.Transparent,
                    Padding = new Thickness(10, 5, 10, 5),
                    Child = cellPanel,
                };
                if (isHeader && cellPanel.Children.OfType<TextBlock>().FirstOrDefault() is { } head)
                    head.FontWeight = FontWeights.SemiBold;
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return grid;
    }

    static FrameworkElement Html(string html, Ctx ctx)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 8) };

        // 徽章/居中图片：<p align=center><img src=...></p>
        var images = ImgSrc.Matches(html);
        if (images.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 2, 0, 6) };
            foreach (Match match in images)
                wrap.Children.Add(Image(Resolve(match.Groups[1].Value, ctx.Repo), null, maxHeight: 28));
            panel.Children.Add(wrap);
        }

        var text = Tag.Replace(html, " ")
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Trim();
        text = Regex.Replace(text, @"\s+", " ");
        if (text.Length > 0 && images.Count == 0) panel.Children.Add(Muted(text));
        return panel;
    }

    // ── 行内 ──────────────────────────────────────────────────────────

    static void AddInlines(InlineCollection target, ContainerInline? container, Ctx ctx)
    {
        if (container is null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case EmphasisInline emphasis:
                {
                    if (emphasis.DelimiterChar == '~')
                    {
                        var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                        AddInlines(span.Inlines, emphasis, ctx);
                        target.Add(span);
                    }
                    else if (emphasis.DelimiterCount >= 2)
                    {
                        var bold = new Bold();
                        AddInlines(bold.Inlines, emphasis, ctx);
                        target.Add(bold);
                    }
                    else
                    {
                        var italic = new Italic();
                        AddInlines(italic.Inlines, emphasis, ctx);
                        target.Add(italic);
                    }
                    break;
                }
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = Mono,
                        FontSize = 12.5,
                        Background = BrushOf("BgSubtle", "#EFF1F3"),
                        Foreground = BrushOf("AccentText", "#0550AE"),
                    });
                    break;
                case LinkInline link when link.IsImage:
                {
                    var el = Image(Resolve(link.Url ?? "", ctx.Repo), link.Title, maxHeight: 360);
                    if (el is UIElement ui) target.Add(new InlineUIContainer(ui));
                    break;
                }
                case LinkInline link:
                {
                    var hyper = new Hyperlink { Foreground = BrushOf("AccentText", "#0969DA"), TextDecorations = null };
                    AddInlines(hyper.Inlines, link, ctx);
                    var url = ResolveLink(link.Url, ctx.Repo);
                    hyper.Click += (_, _) => Open(url);
                    target.Add(hyper);
                    break;
                }
                case AutolinkInline auto:
                {
                    var hyper = new Hyperlink(new Run(auto.Url))
                    {
                        Foreground = BrushOf("AccentText", "#0969DA"),
                        TextDecorations = null,
                    };
                    hyper.Click += (_, _) => Open(auto.Url.StartsWith("http") ? auto.Url : "mailto:" + auto.Url);
                    target.Add(hyper);
                    break;
                }
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case HtmlInline html:
                {
                    var text = Tag.Replace(html.Tag, " ").Trim();
                    if (text.Length > 0) target.Add(new Run(text));
                    break;
                }
                case ContainerInline nested:
                    AddInlines(target, nested, ctx);
                    break;
            }
        }
    }

    static string PlainInline(ContainerInline? container)
    {
        if (container is null) return "";
        var parts = new List<string>();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: parts.Add(literal.Content.ToString()); break;
                case CodeInline code: parts.Add(code.Content); break;
                case ContainerInline nested: parts.Add(PlainInline(nested)); break;
            }
        }
        return string.Concat(parts);
    }

    // ── 杂项 ──────────────────────────────────────────────────────────

    static TextBlock Muted(string text) => new()
    {
        Text = text,
        FontSize = 13.5,
        LineHeight = 24,
        TextWrapping = TextWrapping.Wrap,
        Foreground = BrushOf("TextMuted", "#59636E"),
        Margin = new Thickness(0, 2, 0, 8),
    };

    static FrameworkElement Image(string url, string? alt, double maxHeight)
    {
        var img = new Image
        {
            MaxHeight = maxHeight,
            MaxWidth = 640,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 2, 8, 2),
            ToolTip = alt ?? url,
        };
        // 图片绝不阻塞渲染：先占位，后台下载完再塞进来（README 的徽章常常在 shields.io）。
        _ = LoadImageAsync(img, url);
        return img;
    }

    static async Task LoadImageAsync(Image img, string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            await img.Dispatcher.InvokeAsync(() =>
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                img.Source = bmp;
            });
        }
        catch { /* 图挂了就留空白 */ }
    }
    static string Resolve(string url, string repo)
    {
        var u = url.Trim();
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return u;
        return $"https://raw.githubusercontent.com/{repo}/HEAD/{u.TrimStart('.', '/')}";
    }

    static string ResolveLink(string? url, string repo)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return $"https://github.com/{repo}";
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return u;
        if (u.StartsWith('#')) return $"https://github.com/{repo}#{u[1..]}";
        return $"https://github.com/{repo}/blob/HEAD/{u.TrimStart('.', '/')}";
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 打不开就算了 */ }
    }
}
