using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Pad.Core;

namespace Pad.Views;

/// <summary>
/// 插件详情窗口：按插件市场的版面渲染（图标 / 名字 / 版本 · 可更新 / 简介 / 标签 / 动作），
/// 不再是 markdown 字段列表，也不塞在页面里当浮层。
/// </summary>
public partial class PluginDetailWindow : Window
{
    readonly PluginRow _row;
    readonly Action? _install;
    readonly Action<string>? _installVersion;
    readonly Func<Task<IReadOnlyList<string>>>? _loadVersions;

    string? _repo;
    Task? _readmeLoad;
    bool _showingReadme;
    bool _switchingLang;
    string _readmeLang = "zh";
    readonly Dictionary<string, string> _readmeTexts = new(StringComparer.OrdinalIgnoreCase);

    public event Action<PluginRow>? UpdateRequested;
    public event Action<PluginRow>? RemoveRequested;
    public event Action<PluginRow>? OpenDirRequested;

    public PluginDetailWindow(PluginRow row, Action? install = null,
        Func<Task<IReadOnlyList<string>>>? versions = null, Action<string>? installVersion = null)
    {
        InitializeComponent();
        Loaded += (_, _) => Fx.DialogIn(this);
        _row = row;
        _install = install;
        _loadVersions = versions;
        _installVersion = installVersion;

        DetailIcon.IconPath = row.IconPath;
        DetailIcon.Spec = row.Spec;
        DetailIcon.Kind = row.Kind;
        DetailName.Text = row.Title;
        DetailSpec.Text = row.Spec;
        DetailVersion.Text = row.VersionText;
        if (_loadVersions is not null) VersionPickRow.Visibility = Visibility.Collapsed;
        DetailUpdateBadge.Visibility = row.Updatable ? Visibility.Visible : Visibility.Collapsed;
        DetailUpdateText.Text = row.UpdateText;
        DetailDesc.Text = row.Description.Length > 0 ? row.Description : "包里没写简介。";
        DetailTags.ItemsSource = row.Tags;
        if (row.PreviewImages.Count > 0)
        {
            DetailShots.ItemsSource = row.PreviewImages;
            DetailShots.Visibility = Visibility.Visible;
        }
        else
        {
            ShotsLoaded = LoadRepoShotsAsync();
        }

        InfoVersion.Text = row.VersionText;
        InfoLatest.Text = row.Latest.Length > 0 ? row.Latest : "—";
        InfoProfile.Text = row.Profile.Length > 0 ? row.Profile : "—";
        var repo = LaunchPolicy.GithubRepo(row.Repository);
        DetailMeta.Text = repo ?? (row.Trial ? "试验（仅本次运行）" : "—");
        _repo = repo;
        if (_repo is null) ReadmeBtn.Visibility = Visibility.Collapsed;
        ReadmeHead.Text = row.Title + " · README";
        _ = EnsureReadmeAsync();          // 打开详情就后台取 README，点「详情」时直接渲染
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(MarkdownWpf.Warmup));   // UI 线程空闲时预热渲染路径（首次约 300ms）

        if (_install is not null || _installVersion is not null)
        {
            UpdateBtn.Visibility = Visibility.Visible;
            UpdateBtn.Content = "安装";
            if (_loadVersions is not null) VersionsLoaded = LoadVersionsAsync();
        }
        else
        {
            UpdateBtn.Visibility = row.ShowUpdateButton ? Visibility.Visible : Visibility.Collapsed;
            UpdateBtn.Content = row.Version.Length == 0 ? "安装" : "更新";
        }
        FavBtn.Content = row.Favorite ? "★ 已收藏" : "☆ 收藏";
    }

    /// <summary>仓库预览图加载完成（没有仓库/没有图时也完成）；--shot-page plugin-detail 会等它再截图。</summary>
    public Task ShotsLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>版本列表加载完成（商店模式；--shot-page market-detail 会等它）。</summary>
    public Task VersionsLoaded { get; private set; } = Task.CompletedTask;

    async Task LoadRepoShotsAsync()
    {
        try
        {
            DetailShotHint.Visibility = Visibility.Visible;
            var root = AppState.Current.Launcher.Root;
            var shots = await RepoShots.EnsureAsync(root, _row.Spec, _row.Repository);
            if (shots.Count > 0)
            {
                DetailShots.ItemsSource = shots;
                DetailShots.Visibility = Visibility.Visible;
            }
        }
        catch { /* 没网/仓库没有图：不显示预览 */ }
        finally { DetailShotHint.Visibility = Visibility.Collapsed; }
    }

    void Fav_Click(object sender, RoutedEventArgs e)
    {
        var on = Favorites.Toggle(AppState.Current.Launcher.Root, _row.Spec);
        _row.Favorite = on;
        FavBtn.Content = on ? "★ 已收藏" : "☆ 收藏";
    }

    async Task LoadVersionsAsync()
    {
        try
        {
            var versions = await _loadVersions!();
            if (versions.Count == 0) return;
            VersionPick.ItemsSource = versions;
            VersionPick.SelectedIndex = 0;
            VersionPickRow.Visibility = Visibility.Visible;
        }
        catch { /* 拿不到版本列表就用默认版本安装 */ }
    }

    void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_installVersion is { } installVer)
        {
            var version = VersionPick.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(version)) version = VersionPick.Items.Count > 0
                ? VersionPick.Items[0] as string ?? ""
                : "";
            Close();
            if (version.Length > 0) { installVer(version); return; }
            // 版本列表还没拿到（或 npm 没返回版本）时别静默：退回“装最新”。
            if (_install is { } latest) { latest(); return; }
            return;
        }
        if (_install is { } install)
        {
            Close();
            install();
            return;
        }
        UpdateRequested?.Invoke(_row);
        Close();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        RemoveRequested?.Invoke(_row);
        Close();
    }

    void Dir_Click(object sender, RoutedEventArgs e)
    {
        OpenDirRequested?.Invoke(_row);
        Close();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();


    // ── README 页（丝滑切换） ─────────────────────────────────────────────

    void Readme_Click(object sender, RoutedEventArgs e) => ShowReadme(true);

    void ReadmeBack_Click(object sender, RoutedEventArgs e) => ShowReadme(false);

    void Lang_Checked(object sender, RoutedEventArgs e)
    {
        if (_switchingLang || !IsLoaded || _repo is null) return;
        if (sender is not FrameworkElement { Tag: string lang }) return;
        _readmeLang = lang;
        _ = LoadReadmeAsync(lang);
    }

    /// <summary>详情 ↔ README：横向滑动 + 淡入淡出，300ms ease-out。</summary>
    void ShowReadme(bool show)
    {
        if (_showingReadme == show || _repo is null) return;
        _showingReadme = show;

        var dur = new Duration(TimeSpan.FromMilliseconds(300));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (show)
        {
            ReadmePage.Visibility = Visibility.Visible;
            ReadmePage.IsHitTestVisible = true;
            DetailPage.IsHitTestVisible = false;
            ReadmeShift.X = 56;
            ReadmePage.Opacity = 0;
            ReadmeShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(56, 0, dur) { EasingFunction = ease });
            ReadmePage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
            DetailShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, -48, dur) { EasingFunction = ease });
            DetailPage.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, dur));
            _ = LoadReadmeAsync(_readmeLang);
        }
        else
        {
            ReadmePage.IsHitTestVisible = false;
            DetailPage.IsHitTestVisible = true;
            var fade = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
            fade.Completed += (_, _) => ReadmePage.Visibility = Visibility.Collapsed;
            ReadmePage.BeginAnimation(OpacityProperty, fade);
            ReadmeShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 56, dur) { EasingFunction = ease });
            DetailPage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
            DetailShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-48, 0, dur) { EasingFunction = ease });
        }
    }

    Task EnsureReadmeAsync()
    {
        if (_readmeLoad is null) _readmeLoad = FetchReadmeAsync();
        return _readmeLoad;
    }

    async Task FetchReadmeAsync()
    {
        if (_repo is null) return;
        try
        {
            var zhTask = ReadmeFetch.TryAsync(_repo, "zh");
            var enTask = ReadmeFetch.TryAsync(_repo, "en");
            await Task.WhenAll(zhTask, enTask);
            var zh = zhTask.Result;
            var en = enTask.Result;
            if (zh is not null) _readmeTexts["zh"] = zh;
            if (en is not null) _readmeTexts["en"] = en;
        }
        catch { /* 拿不到就当没有 README */ }
    }

    void ReadmeRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_repo is null) return;
        ReadmeFetch.Forget(_repo, "zh");
        ReadmeFetch.Forget(_repo, "en");
        _readmeLoad = null;
        _readmeTexts.Clear();
        _ = LoadReadmeAsync(_readmeLang);
    }

    async Task LoadReadmeAsync(string lang)
    {
        if (_repo is null) return;

        ReadmeFail.Visibility = Visibility.Visible;
        ReadmeStatus.Text = "正在读仓库 README…";
        ReadmeRetry.Visibility = Visibility.Collapsed;
        await EnsureReadmeAsync();

        LangZh.IsEnabled = _readmeTexts.ContainsKey("zh");
        LangEn.IsEnabled = _readmeTexts.ContainsKey("en");
        if (_readmeTexts.Count == 0)
        {
            ReadmeStatus.Text = "拿不到 README（网络不通，或这个仓库没有）。";
            ReadmeRetry.Visibility = Visibility.Visible;
            return;
        }

        if (!_readmeTexts.ContainsKey(lang))
        {
            lang = _readmeTexts.ContainsKey("zh") ? "zh" : "en";
            _switchingLang = true;
            if (lang == "zh") LangZh.IsChecked = true; else LangEn.IsChecked = true;
            _switchingLang = false;
        }

        _readmeLang = lang;
        ReadmeFail.Visibility = Visibility.Collapsed;
        await MarkdownWpf.RenderAsync(_readmeTexts[lang], ReadmeHost, _repo);
    }

    /// <summary>离屏把本窗口渲染成 PNG（--shot-page plugin-detail 用；不经屏幕捕获）。</summary>
    public void RenderTo(string path)
    {
        try
        {
            UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(this);
            var w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            var h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);
            rtb.Render(this);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(path);
            png.Save(fs);
            File.WriteAllText(path + ".txt",
                $"logical {ActualWidth}x{ActualHeight}\ndpi {dpi.DpiScaleX}x{dpi.DpiScaleY}\npixels {w}x{h}\n");
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(path + ".txt", ex.ToString()); } catch { }
        }
    }
}
