using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Pad.Views;

/// <summary>
/// 极简 Markdown 渲染：只覆盖 README 常见语法（标题 / 段落 / 列表 / 代码块 / 引用 / 分隔线 / 行内粗体·代码·链接）。
/// 不引第三方库，渲染成一组 TextBlock / Border，跟着窗口主题走。
/// </summary>
public static class MarkdownView
{
    static readonly Regex InlinePattern = new(
        @"(\*\*[^*]+\*\*|`[^`]+`|!\[[^\]]*\]\([^)]+\)|\[[^\]]+\]\([^)]+\))",
        RegexOptions.Compiled);

    static readonly Regex LinkPattern = new(@"^\[([^\]]+)\]\(([^)]+)\)$", RegexOptions.Compiled);
    static readonly Regex ImagePattern = new(@"^!\[([^\]]*)\]\(([^)]+)\)$", RegexOptions.Compiled);

    static object Res(string key, object fallback)
    {
        try { return Application.Current?.TryFindResource(key) ?? fallback; }
        catch { return fallback; }
    }

    static Brush BrushOf(string key, string fallback) => Res(key, new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback))) as Brush ?? Brushes.Gray;
    static double SizeOf(string key, double fallback) => Res(key, fallback) is double d ? d : fallback;
    static FontFamily FontOf(string key, string fallback) => Res(key, new FontFamily(fallback)) as FontFamily ?? new FontFamily(fallback);

    public static void Render(string markdown, Panel host)
    {
        host.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            host.Children.Add(new TextBlock
            {
                Text = "这个仓库没有 README。",
                Foreground = BrushOf("TextMuted", "#848C92"),
                FontSize = SizeOf("FsBody", 13),
                Margin = new Thickness(0, 8, 0, 0),
            });
            return;
        }

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;
        var rendered = 0;
        while (i < lines.Length && rendered < 3000)
        {
            var raw = lines[i];
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            // 代码块
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;
                host.Children.Add(CodeBlock(string.Join("\n", code)));
                rendered++;
                continue;
            }

            // 标题
            var hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes is >= 1 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
            {
                host.Children.Add(Heading(trimmed[(hashes + 1)..].Trim(), hashes));
                i++;
                rendered++;
                continue;
            }

            // 分隔线
            if (trimmed is "---" or "***" or "___")
            {
                host.Children.Add(new Border
                {
                    Height = 1,
                    Background = BrushOf("LineDivider", "#E3E8EB"),
                    Margin = new Thickness(0, 10, 0, 10),
                });
                i++;
                continue;
            }

            // 引用
            if (trimmed.StartsWith('>'))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    quote.Add(lines[i].TrimStart()[1..].TrimStart());
                    i++;
                }
                var quoteText = string.Join("\n", quote);
                host.Children.Add(new Border
                {
                    BorderBrush = BrushOf("AccentSoft", "#C6E2EB"),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 2, 0, 2),
                    Margin = new Thickness(0, 6, 0, 6),
                    Child = Body(quoteText, muted: true),
                });
                rendered++;
                continue;
            }

            // 列表
            if (IsListItem(trimmed, out var marker, out var itemText))
            {
                host.Children.Add(ListRow(marker, itemText));
                i++;
                rendered++;
                continue;
            }

            // 图片（不加载网络图，给个占位）
            var img = ImagePattern.Match(trimmed);
            if (img.Success)
            {
                var alt = img.Groups[1].Value.Trim();
                host.Children.Add(new Border
                {
                    Background = BrushOf("BgSubtle", "#EAEFF3"),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 5, 0, 5),
                    Child = new TextBlock
                    {
                        Text = alt.Length > 0 ? $"图片 · {alt}" : "图片",
                        Foreground = BrushOf("TextMuted", "#848C92"),
                        FontSize = SizeOf("FsSmall", 12),
                    },
                });
                i++;
                continue;
            }

            // 表格行 / 普通段落
            if (trimmed.Length == 0) { i++; continue; }

            if (trimmed.StartsWith('|'))
            {
                host.Children.Add(new TextBlock
                {
                    Text = trimmed,
                    FontFamily = FontOf("FontMono", "Consolas"),
                    FontSize = SizeOf("FsSmall", 12),
                    Foreground = BrushOf("TextSecondary", "#414A52"),
                    TextWrapping = TextWrapping.NoWrap,
                    Margin = new Thickness(0, 1, 0, 1),
                });
                i++;
                rendered++;
                continue;
            }

            var para = new List<string> { trimmed };
            i++;
            while (i < lines.Length)
            {
                var next = lines[i].TrimEnd();
                var nt = next.TrimStart();
                if (nt.Length == 0 || nt.StartsWith('#') || nt.StartsWith("```") || nt.StartsWith('>') ||
                    nt.StartsWith('|') || IsListItem(nt, out _, out _) ||
                    nt is "---" or "***" or "___" || ImagePattern.IsMatch(nt))
                    break;
                para.Add(nt);
                i++;
            }
            host.Children.Add(Body(string.Join("\n", para), muted: false));
            rendered++;
        }
    }

    static bool IsListItem(string trimmed, out string marker, out string text)
    {
        marker = "";
        text = "";
        if (trimmed.Length < 3) return false;
        if (trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
        {
            marker = "•";
            text = trimmed[2..].Trim();
            return true;
        }
        var m = Regex.Match(trimmed, @"^(\d{1,3}[.)])\s+(.*)$");
        if (!m.Success) return false;
        marker = m.Groups[1].Value;
        text = m.Groups[2].Value;
        return true;
    }

    static TextBlock Heading(string text, int level) => new()
    {
        Text = Plain(text),
        FontSize = level switch { 1 => 26d, 2 => 21d, 3 => 17d, _ => 15d },
        FontWeight = FontWeights.SemiBold,
        Foreground = BrushOf("TextPrimary", "#19222A"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, level <= 2 ? 18 : 12, 0, 6),
    };

    static TextBlock Body(string text, bool muted)
    {
        var block = new TextBlock
        {
            FontSize = SizeOf("FsBody", 13),
            LineHeight = 24,
            TextWrapping = TextWrapping.Wrap,
            Foreground = muted ? BrushOf("TextMuted", "#848C92") : BrushOf("TextSecondary", "#414A52"),
            Margin = new Thickness(0, 3, 0, 3),
        };
        AddInline(block, text);
        return block;
    }

    static Grid ListRow(string marker, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var mark = new TextBlock
        {
            Text = marker,
            FontSize = SizeOf("FsBody", 13),
            Foreground = BrushOf("TextMuted", "#848C92"),
            Margin = new Thickness(2, 0, 0, 0),
        };
        var body = Body(text, muted: false);
        body.Margin = new Thickness(0);
        Grid.SetColumn(mark, 0);
        Grid.SetColumn(body, 1);
        grid.Children.Add(mark);
        grid.Children.Add(body);
        return grid;
    }

    static Border CodeBlock(string code) => new()
    {
        Background = BrushOf("BgSubtle", "#EAEFF3"),
        BorderBrush = BrushOf("LineBorder", "#D5DDE2"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(12, 9, 12, 9),
        Margin = new Thickness(0, 6, 0, 6),
        Child = new TextBlock
        {
            Text = code,
            FontFamily = FontOf("FontMono", "Consolas"),
            FontSize = SizeOf("FsSmall", 12),
            Foreground = BrushOf("TextSecondary", "#414A52"),
            TextWrapping = TextWrapping.NoWrap,
        },
    };

    static string Plain(string text) => InlinePattern.Replace(text, m =>
    {
        var s = m.Value;
        if (s.StartsWith("**") && s.EndsWith("**")) return s[2..^2];
        if (s.StartsWith('`') && s.EndsWith('`')) return s[1..^1];
        var link = LinkPattern.Match(s);
        if (link.Success) return link.Groups[1].Value;
        var img = ImagePattern.Match(s);
        if (img.Success) return img.Groups[1].Value;
        return s;
    });

    static void AddInline(TextBlock block, string text)
    {
        var pos = 0;
        foreach (Match m in InlinePattern.Matches(text))
        {
            if (m.Index > pos) block.Inlines.Add(new Run(text[pos..m.Index]));

            var s = m.Value;
            if (s.StartsWith("**") && s.EndsWith("**"))
            {
                block.Inlines.Add(new Bold(new Run(s[2..^2])) { Foreground = BrushOf("TextPrimary", "#19222A") });
            }
            else if (s.StartsWith('`') && s.EndsWith('`'))
            {
                block.Inlines.Add(new Run(s[1..^1])
                {
                    FontFamily = FontOf("FontMono", "Consolas"),
                    Background = BrushOf("BgSubtle", "#EAEFF3"),
                    Foreground = BrushOf("AccentText", "#006177"),
                });
            }
            else if (ImagePattern.Match(s) is { Success: true } img)
            {
                var alt = img.Groups[1].Value;
                if (alt.Length > 0) block.Inlines.Add(new Run(alt) { FontStyle = FontStyles.Italic });
            }
            else if (LinkPattern.Match(s) is { Success: true } link)
            {
                var url = link.Groups[2].Value;
                var hyper = new Hyperlink(new Run(link.Groups[1].Value))
                {
                    Foreground = BrushOf("AccentText", "#006177"),
                    TextDecorations = null,
                };
                hyper.Click += (_, _) => Open(url);
                block.Inlines.Add(hyper);
            }
            else block.Inlines.Add(new Run(s));

            pos = m.Index + m.Length;
        }
        if (pos < text.Length) block.Inlines.Add(new Run(text[pos..]));
    }

    static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* 打不开就算了 */ }
    }
}
