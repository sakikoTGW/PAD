using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pad.Core;

/// <summary>
/// 所有图标的唯一入口：实例、整合包、插件、bundle、桌面壳、下载任务都走这里。
/// 规则固定：用户登记/对象自带 → 本地缓存 → 网络（插件包）→ 占位（官方=鲸鱼，其他=首字母）。
/// 缓存落在 <c>&lt;root&gt;/icons</c>，索引是 <c>icons.json</c>；名字以 key 为准，不按调用点各写一套。
/// </summary>
public static class IconService
{
    static readonly object Gate = new();
    static readonly Dictionary<string, string> Index = new(StringComparer.OrdinalIgnoreCase);
    static string? _loadedRoot;

    static readonly Regex SpecRe = new(
        @"(?<name>(?:@[a-z0-9][\w.-]*/)?[a-z0-9][\w.-]*)@(?<ver>\d[^\s]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Regex BareRe = new(
        @"^(?<name>(?:@[a-z0-9][\w.-]*/)?[a-z0-9][\w.-]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string CacheDir(string root) => Path.Combine(root, "icons");

    static string IndexPath(string root) => Path.Combine(CacheDir(root), "icons.json");

    /// <summary>官方包（只有它们允许用黑鲸鱼占位）。</summary>
    public static bool IsOfficialName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && (name.StartsWith("@deepseek-ai/", StringComparison.OrdinalIgnoreCase)
            || name.Equals("@deepseek-ai/dsh-base", StringComparison.OrdinalIgnoreCase));

    /// <summary>同步查缓存；没有就返回 null（绝不联网）。</summary>
    public static string? Resolve(string root, string key)
    {
        LoadIndex(root);
        lock (Gate)
        {
            if (!Index.TryGetValue(key, out var rel)) return null;
            var full = Path.Combine(CacheDir(root), rel);
            return File.Exists(full) ? full : null;
        }
    }

    /// <summary>把一个已有图片登记到统一缓存，返回缓存路径（源文件不在缓存里时复制一份）。</summary>
    public static string? Register(string root, string key, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        try
        {
            LoadIndex(root);
            var dir = CacheDir(root);
            Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp")) ext = ".png";
            var rel = Sha1(key)[..16] + ext;
            var dest = Path.Combine(dir, rel);
            if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
                File.Copy(sourcePath, dest, overwrite: true);
            lock (Gate) Index[key] = rel;
            SaveIndex(root);
            return dest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>首字母占位图（灰底方块）；官方包不生成，让界面用鲸鱼默认图。</summary>
    public static string? Placeholder(string root, string text)
    {
        var letter = FirstLetter(text);
        if (letter.Length == 0) return null;
        var key = "ph:" + letter;
        if (Resolve(root, key) is { } hit) return hit;
        try
        {
            var dir = CacheDir(root);
            Directory.CreateDirectory(dir);
            var rel = "ph-" + Sha1(letter)[..12] + ".png";
            var file = Path.Combine(dir, rel);
            if (!File.Exists(file)) WriteLetterPng(file, letter);
            lock (Gate) Index[key] = rel;
            SaveIndex(root);
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>下载任务的图标：任务自带 → 实例 mark → 包缓存 → 占位（官方返回 null，让鲸鱼兜底）。</summary>
    public static string? ForJob(Launcher launcher, JobRecord job)
    {
        var key = "job2:" + job.Id;
        if (Resolve(launcher.Root, key) is { } cached) return cached;

        // 插件类任务先看包：实例默认 mark 只是品牌占位，拿它会把所有插件画成同一张图。
        var spec = ParseSpec(job.Label) ?? ParseSpec(job.Title);
        if (spec is not null)
        {
            var cachedIcon = NpmPackageIcon.TryCached(launcher.Root, spec.Value.Name);
            if (cachedIcon is not null) return Register(launcher.Root, key, cachedIcon);
            if (IsOfficialName(spec.Value.Name)) return null;
            return Placeholder(launcher.Root, spec.Value.Name);
        }

        if (!string.IsNullOrWhiteSpace(job.Icon) && File.Exists(job.Icon))
            return Register(launcher.Root, key, job.Icon);

        if (!string.IsNullOrWhiteSpace(job.Instance))
        {
            try
            {
                var inst = launcher.Instance(job.Instance);
                var mark = inst is null ? null : InstIcon.MarkFile(inst);
                if (!string.IsNullOrWhiteSpace(mark))
                    return Register(launcher.Root, key, mark);
            }
            catch { /* 实例没了就继续往下 */ }
        }

        return Placeholder(launcher.Root, job.Kind);
    }

    /// <summary>任务没图标时去网络补一次（插件包），成功返回缓存路径。</summary>
    public static async Task<string?> FillJobAsync(Launcher launcher, JobRecord job, CancellationToken ct)
    {
        var spec = ParseSpec(job.Label) ?? ParseSpec(job.Title);
        if (spec is null) return null;
        try
        {
            var path = await NpmPackageIcon.EnsureAsync(launcher.Root, spec.Value.Name, spec.Value.Version, ct);
            return path is null ? null : Register(launcher.Root, "job:" + job.Id, path);
        }
        catch { return null; }
    }

    public static (string Name, string Version)? ParseSpec(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = SpecRe.Match(text);
        if (m.Success) return (m.Groups["name"].Value, m.Groups["ver"].Value);
        m = BareRe.Match(text.Trim());
        return m.Success ? (m.Groups["name"].Value, "") : null;
    }

    // ---- 索引 / 占位绘制 -------------------------------------------------

    static void LoadIndex(string root)
    {
        lock (Gate)
        {
            if (string.Equals(_loadedRoot, root, StringComparison.OrdinalIgnoreCase)) return;
            _loadedRoot = root;
            Index.Clear();
        }
        try
        {
            var file = IndexPath(root);
            if (!File.Exists(file)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            if (map is null) return;
            lock (Gate)
                foreach (var (k, v) in map) Index[k] = v;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    static void SaveIndex(string root)
    {
        Dictionary<string, string> snap;
        lock (Gate) snap = new Dictionary<string, string>(Index, StringComparer.OrdinalIgnoreCase);
        try
        {
            Directory.CreateDirectory(CacheDir(root));
            File.WriteAllText(IndexPath(root), JsonSerializer.Serialize(snap));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string Sha1(string text)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static string FirstLetter(string text)
    {
        foreach (var ch in text)
            if (char.IsAsciiLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
        return "";
    }

    static void WriteLetterPng(string file, string letter)
    {
        var seed = Sha1(letter);
        var hue = (byte)(Convert.ToInt32(seed[..2], 16) % 360);
        var bg = FromHsl(hue, 0.18, 0.88);
        var fg = FromHsl(hue, 0.45, 0.32);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bgBrush = new SolidColorBrush(bg); bgBrush.Freeze();
            dc.DrawRoundedRectangle(bgBrush, null, new Rect(0, 0, 64, 64), 16, 16);
            var ft = new FormattedText(letter, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("HarmonyOS Sans SC, HarmonyOS Sans, HarmonySans, Microsoft YaHei UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.SemiBold,
                    FontStretches.Normal),
                32, new SolidColorBrush(fg), 1.0);
            dc.DrawText(ft, new Point((64 - ft.Width) / 2, (64 - ft.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    static Color FromHsl(byte hue, double sat, double light)
    {
        var h = hue / 360.0;
        var c = (1 - Math.Abs(2 * light - 1)) * sat;
        var x = c * (1 - Math.Abs(h * 6 % 2 - 1));
        var m = light - c / 2;
        var (r, g, b) = h switch
        {
            < 1.0 / 6 => (c, x, 0.0),
            < 2.0 / 6 => (x, c, 0.0),
            < 3.0 / 6 => (0.0, c, x),
            < 4.0 / 6 => (0.0, x, c),
            < 5.0 / 6 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
