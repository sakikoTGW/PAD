using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Pad.Core;
using Pad.Views;

namespace Pad;

public partial class MainWindow : Window
{
    readonly Dictionary<string, UIElement> _pages = [];
    string _back = "launch";
    string _page = "launch";
    DispatcherFrame? _msgFrame;
    bool _msgAccepted;
    bool _adoptBusy;

    static readonly Dictionary<string, string> PageNames = new()
    {
        ["launch"] = "启动",
        ["manage"] = "管理",
        ["tasks"] = "任务",
        ["download"] = "功能",
        ["settings"] = "设置",
    };

    const double ShellRadius = 10;

    public MainWindow()
    {
        Loaded += (_, _) => Pad.Core.Market.Prefetch();
        InitializeComponent();
        _progressIdle.Tick += (_, _) =>
        {
            _progressIdle.Stop();
            ProgressDone(0);
        };
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnMdLink));
        PreviewKeyDown += Window_PreviewKeyDown;
        AllowDrop = true;
        DragOver += Window_DragOver;
        Drop += Window_Drop;
        StageRailList.CollapseClicked += () => ToggleRail();
        // ClipToBounds is rectangular; CornerRadius alone does not clip children. Without this
        // clip the card reads as a square once DWM HWND rounding is off for the morph surplus.
        OuterShell.SizeChanged += (_, _) => SyncShellClip();
        ApplyWindowPrefs();
        ApplyBrand();
        ApplyType();
        Show("launch");
        Loaded += (_, _) => HookRecentHotKey();
        // 等主窗口真正画出来再开浮动窗口：Loaded 里直接 Show 子窗口，冷启动/独立 root 时会被吞掉。
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(OpenRequestedWindow), DispatcherPriority.ApplicationIdle);
        Closed += (_, _) =>
        {
            _recentHotKey?.Dispose();
            _recentHotKey = null;
        };
        Loaded += (_, _) =>
        {
            SyncShellClip();
            UiWatchdog.QuietFor(TimeSpan.FromSeconds(15));
            MoveTabPill(CurrentTab());
            ApplyBrand();
            ApplyType();
            ApplyWindowPrefs();
            if (AppState.Current.Settings.Other.LoadWarning is { Length: > 0 } warn)
                Toast(warn, bad: true);
            if (App.ShotPath is null) AutoReconcileHome();
            if (App.ShotPath is null) PickupSidecarPacks();
            if (App.ShotPage != "launch" || App.ShotPath is not null)
            {
                var tab = App.ShotPage switch
                {
                    "manage" or "manage-edit" or "manage-open" or "manage-project" => TabManage,
                    // 注意：`X or _ when G` 的 when 是整个 arm 的守卫，`relations` 本身不含
                    // "relations-" 会被守卫否决——所以这里用显式布尔，不玩模式组合。
                    _ when App.ShotPage.StartsWith("relations") => TabManage,
                    "tasks" => TabTasks,
                    "download" or "market" or "market-more" or "market-detail" or "market-ranking" or "market-growth" or "market-all" or "market-search" or "market-collapsed" or "dl-tasks" => TabDownload,
                    "settings" or "settings-ui" or "settings-download" or "settings-other"
                        or "settings-launch" or "settings-api" => TabSettings,
                    _ => TabLaunch,
                };
                tab.IsChecked = true;
                if (App.ShotPath is not null)
                    System.IO.File.AppendAllText(App.ShotPath + ".pill.txt",
                        $"set {tab.Name} manage={TabManage.IsChecked} launch={TabLaunch.IsChecked}\n");
                if (App.ShotPage is "versions" or "versions-open")
                    ShowInner("versions", "版本选择");
                // 管理页的项目页也要能截图：不靠指针点侧栏，直接进第一份实例。
                if (App.ShotPage == "manage-project" && Page("manage") is WorkbenchView shotWb)
                    shotWb.OpenProjectForShot();
                if (App.ShotPage == "makepack") ShowInner("makepack", "导出整合包", "versions");
                if (App.ShotPage == "bundles") ShowInner("bundles", "实例与插件", "manage");
                if (App.ShotPage == "relations" || App.ShotPage.StartsWith("relations-")) ShowRelations();
                if (App.ShotPage is "instance" or "instance-settings" or "instance-plugins" or "instance-bundles"
                    or "instance-packs" or "plugin-detail")
                    ShowInner("instance", "版本设置");
                if (App.ShotPage == "tray-hint") ShowTrayHintPreview();
                if (App.ShotPage is "market" or "market-more" or "market-detail" or "market-ranking" or "market-growth" or "market-collapsed" or "market-all" or "market-search" && Page("download") is DownloadView market)
                    market.OpenShelf("plugin");
                if (App.ShotPage == "market-growth" && Page("download") is DownloadView growth)
                    growth.ShowGrowthShot();
                if (App.ShotPage == "market-all" && Page("download") is DownloadView allStore)
                    allStore.ShowAllShot();
                if (App.ShotPage == "market-search" && Page("download") is DownloadView searchStore)
                    searchStore.ShowSearchShot("jingyu");
                if (App.ShotPage == "dl-tasks" && Page("download") is DownloadView dlTasks)
                    dlTasks.OpenShelf("dltasks");
                if (App.ShotPage is "settings-ui" or "settings-download" or "settings-other"
                    or "settings-launch" or "settings-api")
                {
                    TabSettings.IsChecked = true;
                    if (Page("settings") is SettingsView sv)
                        sv.OpenLayer(App.ShotPage["settings-".Length..]);
                }
                // 安装概念（D107）：包库选择器 / 预检两阶段在下方统一走离屏窗口渲染。
            }
            if (App.ShotPath is not null && App.ShotPage is "pack-picker" or "pack-preflight" or "pack-preflight-report")
                _ = ShotPackConceptWindowAsync(App.ShotPath);
            else if (App.ShotPath is not null && App.ShotPage == "adopt-homes")
                ShotAdoptHomes(App.ShotPath);
            else if (App.ShotPath is not null)
                ScheduleShot(App.ShotPath);
            if (App.ShotPath is null) _ = WarmBootAsync();
        };
    }

    /// <summary>
    /// Show the brand boot plate, warm data underneath, then hand brand off to the chrome
    /// mark while the plate slides up. Fast machines still get a short beat so the motion reads.
    /// </summary>
    async Task WarmBootAsync()
    {
        // Short floor so the plate still reads on a cold cache; no artificial stall.
        const int minShowMs = 160;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        BeginBootCover();
        try { await WarmDataAsync(); }
        catch (Exception ex) { App.ReportPage(ex); }
        SetBootProgress(100);
        var left = minShowMs - (int)sw.ElapsedMilliseconds;
        if (left > 0) await Task.Delay(left);
        await PlayBootHandoffAsync();
    }

    void BeginBootCover()
    {
        // Sync title / logo / custom asset with 个性化 before the plate shows.
        ApplyBrand();
        ApplyType();
        ApplySkin();
        CenterBootBrand();
        BrandMark.Opacity = BrandMark.Visibility == Visibility.Visible ? 0 : 1;
        BootPlateShift.BeginAnimation(TranslateTransform.YProperty, null);
        BootBrandShift.BeginAnimation(TranslateTransform.XProperty, null);
        BootBrandShift.BeginAnimation(TranslateTransform.YProperty, null);
        BootPlateShift.Y = 0;
        BootBrandShift.X = 0;
        BootBrandShift.Y = 0;
        BootProgress.BeginAnimation(UIElement.OpacityProperty, null);
        BootProgress.Opacity = 1;
        BootProgress.Value = 0;
        BootCover.Visibility = Visibility.Visible;
        BootCover.Opacity = 1;
        BootCover.IsHitTestVisible = true;
    }

    void SetBootProgress(double value)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetBootProgress(value));
            return;
        }
        BootProgress.Value = Math.Clamp(value, 0, 100);
    }

    async Task PlayBootHandoffAsync()
    {
        // 动效关：直接露顶栏，不跑交接。
        if (!Fx.Animated)
        {
            EndBootCover();
            return;
        }

        ApplyBrand();
        ApplyType();
        ApplySkin();
        CenterBootBrand();
        UpdateLayout();
        BrandMark.UpdateLayout();
        BootBrand.UpdateLayout();

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var beat = Fx.Span(750);
        var fade = Fx.Span(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        BootProgress.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, 0, fade) { EasingFunction = ease });

        var flyBrand = BootBrand.Visibility == Visibility.Visible
            && BrandMark.Visibility == Visibility.Visible;
        if (!flyBrand)
        {
            BootBrand.Visibility = Visibility.Collapsed;
            var plateOnly = new DoubleAnimation(0, ChromeBottomDelta(), beat) { EasingFunction = ease };
            plateOnly.Completed += (_, _) => { EndBootCover(); tcs.TrySetResult(); };
            BootPlateShift.BeginAnimation(TranslateTransform.YProperty, plateOnly);
            await tcs.Task;
            return;
        }

        Point centered;
        try
        {
            var root = (Visual)Content;
            centered = BootBrand.TransformToAncestor(root).Transform(new Point(0, 0));
        }
        catch
        {
            EndBootCover();
            tcs.TrySetResult();
            await tcs.Task;
            return;
        }

        // Switch to bottom anchoring, then offset Y so the mark is still visually centered.
        // It stays there until the plate's bottom slot rises to that point, then rides the edge.
        SeatBootBrand();
        BootBrandShift.X = 0;
        BootBrandShift.Y = 0;
        UpdateLayout();
        BootBrand.UpdateLayout();

        double dx, dy, hold;
        try
        {
            var root = (Visual)Content;
            var seated = BootBrand.TransformToAncestor(root).Transform(new Point(0, 0));
            var mark = BrandMark.TransformToAncestor(root).Transform(new Point(0, 0));
            dx = mark.X - seated.X;
            dy = mark.Y - seated.Y;
            hold = centered.Y - seated.Y;
        }
        catch
        {
            EndBootCover();
            tcs.TrySetResult();
            await tcs.Task;
            return;
        }
        if (hold <= dy) hold = 0;
        BootBrandShift.Y = hold;

        var plate = new DoubleAnimation(0, dy, beat) { EasingFunction = ease };
        EventHandler? frame = null;
        frame = (_, _) => SyncBootBrand(hold, dy, dx);
        CompositionTarget.Rendering += frame;
        plate.Completed += (_, _) =>
        {
            if (frame is not null) CompositionTarget.Rendering -= frame;
            frame = null;
            EndBootCover();
            tcs.TrySetResult();
        };
        BootPlateShift.BeginAnimation(TranslateTransform.YProperty, plate);
        await tcs.Task;
    }

    /// <summary>Keep the mark centered until the plate's bottom slot reaches it, then lock to that edge.</summary>
    void SyncBootBrand(double hold, double dy, double dx)
    {
        var plateY = BootPlateShift.Y;
        if (plateY > hold)
        {
            BootBrandShift.Y = hold - plateY;
            BootBrandShift.X = 0;
            return;
        }
        BootBrandShift.Y = 0;
        var span = dy - hold;
        var u = Math.Abs(span) < 0.5 ? 1 : Math.Clamp((plateY - hold) / span, 0, 1);
        BootBrandShift.X = dx * u;
    }

    void CenterBootBrand()
    {
        BootBrand.VerticalAlignment = VerticalAlignment.Center;
        BootBrand.HorizontalAlignment = HorizontalAlignment.Center;
        BootBrand.Margin = new Thickness(0);
        BootBrand.UpdateLayout();
        var brandH = BootBrand.ActualHeight > 1 ? BootBrand.ActualHeight : 42;
        // Center alignment shifts by half the extra top margin, so the bar clears the mark.
        BootProgress.VerticalAlignment = VerticalAlignment.Center;
        BootProgress.Margin = new Thickness(56, brandH + 36, 56, 0);
    }

    /// <summary>Seat the boot mark on the plate bottom, with the same gap the chrome mark keeps.</summary>
    void SeatBootBrand()
    {
        BootBrand.VerticalAlignment = VerticalAlignment.Bottom;
        BootBrand.HorizontalAlignment = HorizontalAlignment.Center;
        var chromeH = ChromeBar.ActualHeight;
        if (chromeH < 1) chromeH = AppState.Current.Settings.Ui.ChromeHeight;
        BootBrand.UpdateLayout();
        var brandH = BootBrand.ActualHeight > 1 ? BootBrand.ActualHeight : 42;
        var gap = Math.Max(0, (chromeH - brandH) / 2);
        BootBrand.Margin = new Thickness(0, 0, 0, gap);
    }

    double ChromeBottomDelta()
    {
        try
        {
            var root = (Visual)Content;
            var coverBottom = BootCover.TransformToAncestor(root)
                .Transform(new Point(0, BootCover.ActualHeight)).Y;
            var chromeBottom = ChromeBar.TransformToAncestor(root)
                .Transform(new Point(0, ChromeBar.ActualHeight)).Y;
            return chromeBottom - coverBottom;
        }
        catch
        {
            return -Math.Max(0, BootCover.ActualHeight - ChromeBar.ActualHeight);
        }
    }

    void EndBootCover()
    {
        BootPlateShift.BeginAnimation(TranslateTransform.YProperty, null);
        BootBrandShift.BeginAnimation(TranslateTransform.XProperty, null);
        BootBrandShift.BeginAnimation(TranslateTransform.YProperty, null);
        BootProgress.BeginAnimation(UIElement.OpacityProperty, null);
        BootPlateShift.Y = 0;
        BootBrandShift.X = 0;
        BootBrandShift.Y = 0;
        BootProgress.Opacity = 1;
        BootProgress.Value = 0;
        BootCover.Visibility = Visibility.Collapsed;
        BootCover.IsHitTestVisible = false;
        BrandMark.Opacity = 1;
    }

    async Task WarmDataAsync()
    {
        ManageView? manage = null;
        SettingsView? settings = null;
        DownloadView? download = null;

        SetBootProgress(12);
        await Dispatcher.InvokeAsync(() =>
        {
            foreach (var name in new[] { "manage", "download", "settings", "tasks" })
            {
                try
                {
                    var page = Page(name);
                    if (page is FrameworkElement fe && fe.Parent is null)
                        fe.Measure(new Size(Math.Max(ActualWidth, 800), Math.Max(ActualHeight, 520)));
                    switch (page)
                    {
                        case ManageView mv:
                            manage = mv;
                            mv.Prepare();
                            break;
                        case SettingsView sv:
                            settings = sv;
                            break;
                        case DownloadView dv:
                            download = dv;
                            break;
                        case TaskView tv:
                            tv.Prepare();
                            break;
                        case WorkbenchView wb:
                            wb.Prepare();
                            break;
                    }
                }
                catch (Exception ex) { App.ReportPage(ex); }
            }
        });
        SetBootProgress(40);

        // Critical path: settings shelves (terminal / command override). Network market
        // prewarm + icons + packs keep filling in the background after handoff.
        if (settings is not null)
        {
            try { await settings.WarmAsync(); }
            catch (Exception ex) { App.ReportPage(ex); }
        }
        SetBootProgress(78);

        if (manage is not null)
            _ = WarmBg(manage.WarmIconsAsync());
        if (download is not null)
            _ = WarmBg(download.WarmAsync());
        else
            _ = WarmBg(Task.Run(() => MarketPinyinIndex.Prewarm(AppState.Current.Launcher)));
        _ = WarmBg(AppState.Current.ReloadPacks());
        SetBootProgress(96);
    }

    static async Task WarmBg(Task task)
    {
        try { await task; }
        catch (Exception ex) { App.ReportPage(ex); }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WinIcon.Apply(this);
        Dwm.Round(this);
        // CenterScreen only for the first show; leave it on and the first size morph recenters.
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    /// <summary>
    /// Window geometry belongs to the page, not to the pointer (D26). Management carries the
    /// card grid and takes 72%x86% of the work area; every other page stays compact at the
    /// size recorded in pad.json. Both are clamped so a 150% DPI panel never hangs off-screen.
    /// </summary>
    public void ApplyWindowPrefs()
    {
        var ui = AppState.Current.Settings.Ui;
        // No inset margin: a transparent band around the card read as "wider than the window".
        // The visible shape is OuterShell's rounded clip; shadow stays inside that card.
        OuterShell.Margin = new Thickness(0);
        ChromeRow.Height = new GridLength(ui.ChromeHeight);
        // Window.Opacity only exists on a layered window; on an opaque one it is ignored
        // by the compositor, so do not pretend to apply it.
        if (AllowsTransparency) Opacity = ui.WindowOpacity / 100.0;
        ApplyPageSize(_page, animate: false);
        ApplyHiddenTabs();
        ApplySkin();
    }

    /// <summary>Last sizing decision; bundled in the --shot sidecar so a mismatch is provable.</summary>
    string _lastSize = "(none)";

    public bool ApplyPageSize(string page, bool animate, Action? onDone = null,
        Action? onReveal = null)
    {
        var work = SystemParameters.WorkArea;
        var maxW = Math.Max(880, work.Width - 24);
        var maxH = Math.Max(520, work.Height - 24);

        double wantW, wantH;
        if (page is "manage" or "bundles" or "relations")
        {
            wantW = Math.Min(Math.Max(work.Width * 0.72, 1200), maxW);
            wantH = Math.Min(Math.Max(work.Height * 0.86, 760), maxH);
        }
        else
        {
            var ui = AppState.Current.Settings.Ui;
            wantW = Math.Min(ui.WindowWidth, maxW);
            wantH = Math.Min(ui.WindowHeight, maxH);
        }

        if (Math.Abs(Width - wantW) < 1 && Math.Abs(Height - wantH) < 1)
        {
            onDone?.Invoke();
            return false;
        }

        // Pin top-left. If the target would hang off the work area, shrink the target —
        // never slide Left/Top to make room (that read as the anchor jumping on first manage).
        WindowStartupLocation = WindowStartupLocation.Manual;
        var left = Left;
        var top = Top;
        if (left < work.Left) left = work.Left;
        if (top < work.Top) top = work.Top;
        wantW = Math.Min(wantW, Math.Max(1, work.Right - left));
        wantH = Math.Min(wantH, Math.Max(1, work.Bottom - top));

        if (Math.Abs(Width - wantW) < 1 && Math.Abs(Height - wantH) < 1
            && Math.Abs(Left - left) < 1 && Math.Abs(Top - top) < 1)
        {
            onDone?.Invoke();
            return false;
        }

        _lastSize = $"{page}: want {wantW:F0}x{wantH:F0} from {Width:F0}x{Height:F0} animate={animate} fx={Fx.Animated}";

        if (!animate || !Fx.Animated)
        {
            StopShellReveal();
            Left = left;
            Top = top;
            Width = wantW;
            Height = wantH;
            onDone?.Invoke();
            return false;
        }

        var grow = wantW > Width + 0.5 || wantH > Height + 0.5;
        BeginShellReveal(left, top, wantW, wantH, grow, onDone, onReveal);
        return true;
    }

    int _stagedGen;

    /// <summary>
    /// Shell SizeTo morph: lock OuterShell at the old size, tile HWND (transparent surplus),
    /// then grow/shrink the shell. <paramref name="onReveal"/> fires when SizeTo starts
    /// (both directions) so PageIn can run in parallel with the card motion.
    /// </summary>
    void BeginShellReveal(double left, double top, double wantW, double wantH, bool grow,
        Action? onDone, Action? onReveal = null)
    {
        const int beat = 520;
        var gen = ++_stagedGen;
        Fx.StopBounds();

        var m = OuterShell.Margin;
        var fromShellW = Math.Max(1, Width - m.Left - m.Right);
        var fromShellH = Math.Max(1, Height - m.Top - m.Bottom);
        var toShellW = Math.Max(1, wantW - m.Left - m.Right);
        var toShellH = Math.Max(1, wantH - m.Top - m.Bottom);

        Dwm.SetCorners(this, round: false);

        void Land()
        {
            if (gen != _stagedGen) return;
            FinishShellReveal();
            Dwm.SetCorners(this, round: true);
            onDone?.Invoke();
        }

        // Lock at the old size before HWND changes — Stretch must never see the big frame.
        LockShell(fromShellW, fromShellH);
        UpdateLayout();

        if (grow)
        {
            Left = left;
            Top = top;
            Width = wantW;
            Height = wantH;
            Left = left;
            Top = top;
            UpdateLayout();
            // Start SizeTo before PageIn so the first frames are size-only (less hitch).
            Fx.SizeTo(OuterShell, toShellW, toShellH, beat, Land);
            onReveal?.Invoke();
        }
        else
        {
            Fx.SizeTo(OuterShell, toShellW, toShellH, beat, () =>
            {
                if (gen != _stagedGen) return;
                Left = left;
                Top = top;
                Width = wantW;
                Height = wantH;
                Left = left;
                Top = top;
                Land();
            });
            onReveal?.Invoke();
        }
    }

    void LockShell(double width, double height)
    {
        OuterShell.BeginAnimation(FrameworkElement.WidthProperty, null);
        OuterShell.BeginAnimation(FrameworkElement.HeightProperty, null);
        OuterShell.HorizontalAlignment = HorizontalAlignment.Left;
        OuterShell.VerticalAlignment = VerticalAlignment.Top;
        OuterShell.Width = width;
        OuterShell.Height = height;
        SyncShellClip(width, height);
    }

    /// <summary>
    /// Keep the rounded clip in sync with OuterShell. Mutate the live geometry — reallocating
    /// a new RectangleGeometry on every SizeChanged during SizeTo was a steady GC/layout hitch.
    /// </summary>
    void SyncShellClip(double? width = null, double? height = null)
    {
        var w = width ?? OuterShell.ActualWidth;
        var h = height ?? OuterShell.ActualHeight;
        if (w < 1 || h < 1) return;

        if (OuterShell.Clip is RectangleGeometry geo && !geo.IsFrozen)
        {
            var r = geo.Rect;
            if (Math.Abs(r.Width - w) < 0.25 && Math.Abs(r.Height - h) < 0.25
                && Math.Abs(geo.RadiusX - ShellRadius) < 0.1)
                return;
            geo.RadiusX = ShellRadius;
            geo.RadiusY = ShellRadius;
            geo.Rect = new Rect(0, 0, w, h);
            return;
        }

        OuterShell.Clip = new RectangleGeometry(new Rect(0, 0, w, h), ShellRadius, ShellRadius);
    }

    void FinishShellReveal()
    {
        ResetShellLayout();
    }

    void StopShellReveal()
    {
        _stagedGen++;
        Fx.StopBounds();
        if (OuterShell.Clip is RectangleGeometry geo)
            geo.BeginAnimation(RectangleGeometry.RectProperty, null);
        ResetShellLayout();
        Dwm.SetCorners(this, round: true);
    }

    void ResetShellLayout()
    {
        if (OuterShell.Clip is RectangleGeometry geo)
            geo.BeginAnimation(RectangleGeometry.RectProperty, null);
        OuterShell.BeginAnimation(FrameworkElement.WidthProperty, null);
        OuterShell.BeginAnimation(FrameworkElement.HeightProperty, null);
        OuterShell.ClearValue(WidthProperty);
        OuterShell.ClearValue(HeightProperty);
        OuterShell.HorizontalAlignment = HorizontalAlignment.Stretch;
        OuterShell.VerticalAlignment = VerticalAlignment.Stretch;
        SyncShellClip();
    }

    void AfterDelay(int ms, Action run)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            run();
        };
        timer.Start();
    }

    /// <summary>Hide manage/download per pad.json; F12 restores them for this run.</summary>
    void ApplyHiddenTabs()
    {
        var launcher = AppState.Current.Launcher;
        var hidden = AppState.Current.Settings.Ui.HiddenTabs;
        TabManage.Visibility = PadRegistry.PageEnabled(launcher, "manage")
            ? Visibility.Visible : Visibility.Collapsed;
        TabTasks.Visibility = PadRegistry.PageEnabled(launcher, "tasks")
            ? Visibility.Visible : Visibility.Collapsed;
        TabDownload.Visibility = hidden.Contains("download") || !PadRegistry.PageEnabled(launcher, "download")
            ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Resolve a single wallpaper: a random image from wallpaperDir, else wallpaperPath.</summary>
    static string? WallpaperPick(UiPrefs ui)
    {
        if (!string.IsNullOrWhiteSpace(ui.WallpaperDir))
        {
            var dir = Skin.Resolve(ui.WallpaperDir);
            if (dir.Length > 0 && System.IO.Directory.Exists(dir))
            {
                var imgs = System.IO.Directory.EnumerateFiles(dir, "*.*", System.IO.SearchOption.TopDirectoryOnly)
                    .Where(LaunchPolicy.IsImage).ToArray();
                if (imgs.Length > 0) return imgs[Random.Shared.Next(imgs.Length)];
            }
        }
        return ui.WallpaperPath;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (MsgLayer.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                EndMsg(false);
                e.Handled = true;
            }
            else if (e.Key is Key.F11 or Key.F12)
                e.Handled = true;
            return;
        }
        if (MdLayer.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                HideMd();
                e.Handled = true;
            }
            else if (e.Key is Key.F11 or Key.F12)
                e.Handled = true;
            return;
        }
        if (_pages.TryGetValue("instance", out var instPage) && instPage is InstanceView iv && iv.IntroEditOpen)
        {
            if (e.Key == Key.Escape)
            {
                iv.CloseIntroEdit();
                e.Handled = true;
            }
            else if (e.Key is Key.F11 or Key.F12)
                e.Handled = true;
            return;
        }
        if (e.Key == Key.F11 && _page == "versions")
        {
            if (Page("versions") is VersionsView vv)
            {
                vv.ToggleHidden();
                e.Handled = true;
            }
            return;
        }
        if (e.Key != Key.F12) return;
        TabManage.Visibility = Visibility.Visible;
        TabDownload.Visibility = Visibility.Visible;
        if (Page("launch") is LaunchView lv) lv.RevealVersionEntry();
        e.Handled = true;
    }

    public void ApplyBrand()
    {
        var ui = AppState.Current.Settings.Ui;
        BrandMark.Visibility = ui.ShowBrand || ui.ShowLogo
            ? Visibility.Visible : Visibility.Collapsed;
        var logoOn = ui.ShowLogo ? Visibility.Visible : Visibility.Collapsed;
        BrandLogoPlate.Visibility = logoOn;
        BrandLogo.Visibility = logoOn;
        BootLogoPlate.Visibility = logoOn;
        BootLogo.Visibility = logoOn;
        var title = string.IsNullOrWhiteSpace(ui.BrandText) ? "PAD" : ui.BrandText.Trim();
        BrandText.Text = title;
        BootBrandText.Text = title;
        BrandText.Visibility = ui.ShowBrand ? Visibility.Visible : Visibility.Collapsed;
        BootBrandText.Visibility = ui.ShowBrand ? Visibility.Visible : Visibility.Collapsed;
        BootBrand.Visibility = ui.ShowBrand || ui.ShowLogo
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyWindowPrefs();
    }

    double _fontScale = 1;

    /// <summary>设置里的字号和粗细。字号按正文 13 为 1 倍，改已经画出来的字。</summary>
    public void ApplyType()
    {
        var ui = AppState.Current.Settings.Ui;
        var scale = ui.FontSize / 13.0;
        if (scale <= 0) scale = 1;
        var weight = ui.FontWeight switch
        {
            // 雅黑没有 Medium，这一档会换到另一套中文字体，看起来像繁简混用。
            "normal" => FontWeights.Normal,
            "medium" or "semibold" => FontWeights.SemiBold,
            _ => FontWeights.Bold,
        };
        SetFontStep("FsMicro", 11 * scale);
        SetFontStep("FsSmall", 12 * scale);
        SetFontStep("FsBody", 13 * scale);
        SetFontStep("FsRow", 14 * scale);
        SetFontStep("FsTitle", 15 * scale);
        SetFontStep("FsSection", 19 * scale);
        FontSize = 13 * scale;
        FontWeight = weight;
        RescaleFonts(this, _fontScale, scale, weight);
        _fontScale = scale;
    }

    static readonly double[] FontBases = [11, 12, 13, 14, 15, 19];

    static FontFamily? UiFace =>
        Application.Current?.TryFindResource("FontUI") as FontFamily;

    static void SetFontStep(string key, double px)
    {
        if (Application.Current is null) return;
        Application.Current.Resources[key] = px;
    }

    static void RescaleFonts(DependencyObject node, double from, double to, FontWeight weight)
    {
        if (from <= 0) from = 1;
        if (node is TextBlock tb && tb.FontSize > 0)
        {
            var back = tb.FontSize / from;
            if (FontBases.Any(b => Math.Abs(b - back) < 0.45))
                tb.FontSize = back * to;
            tb.FontWeight = weight;
            if (UiFace is not null) tb.FontFamily = UiFace;
        }
        else if (node is Control control && control.FontSize > 0)
        {
            var back = control.FontSize / from;
            if (FontBases.Any(b => Math.Abs(b - back) < 0.45))
                control.FontSize = back * to;
            control.FontWeight = weight;
            if (UiFace is not null) control.FontFamily = UiFace;
        }
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
            RescaleFonts(VisualTreeHelper.GetChild(node, i), from, to, weight);
    }

    /// <summary>Logo + wallpaper + wash opacities from pad.json.</summary>
    public void ApplySkin()
    {
        var ui = AppState.Current.Settings.Ui;
        var bundled = new Uri("pack://application:,,,/Assets/pad-logo.png");
        var logo = Skin.LoadBitmap(ui.LogoPath, bundled);
        BrandLogo.Source = logo;
        BootLogo.Source = logo;

        var paper = Skin.LoadBitmap(WallpaperPick(ui));
        if (paper is null)
        {
            Wallpaper.Visibility = Visibility.Collapsed;
            Wallpaper.Source = null;
            Wallpaper.Effect = null;
        }
        else
        {
            Wallpaper.Source = paper;
            Wallpaper.Stretch = Skin.Fit(ui.WallpaperFit);
            Wallpaper.Opacity = ui.WallpaperOpacity / 100.0;
            Wallpaper.Effect = ui.WallpaperBlur > 0
                ? new System.Windows.Media.Effects.BlurEffect { Radius = ui.WallpaperBlur, KernelType = System.Windows.Media.Effects.KernelType.Gaussian }
                : null;
            Wallpaper.Visibility = Visibility.Visible;
        }

        var page = (Brush)FindResource("BgPage");
        var chrome = (Brush)FindResource("BgChrome");
        PageWash.Background = Skin.Wash(page, ui.ContentOpacity);
        ChromeBar.Background = Skin.Wash(chrome, ui.ChromeOpacity);
        if (Page("download") is DownloadView dv) dv.SyncStoreEdgeFade();
    }

    /// <summary>--shot-page adopt-homes：离屏渲染收编清单，不扫本机、不抢前台。</summary>
    void ShotAdoptHomes(string path)
    {
        var demo = new[]
        {
            new PendingHome(@"E:\DSH-Home", 3, 22, "known"),
            new PendingHome(@"C:\Users\Brs\.dsh", 2, 2, "known"),
            new PendingHome(@"C:\Users\Brs\AppData\Roaming\dsh-desktop\harness", 1, 5, "desktop"),
            new PendingHome(@"E:\dsh-tui\dsh-home", 1, 0, "known"),
        };
        var win = new Views.AdoptHomesWindow(demo)
        {
            Owner = this,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
        };
        win.Show();
        win.UpdateLayout();
        win.RenderTo(path);
        win.Close();
        if (App.ShotExit) Close();
    }

    /// <summary>
    /// --shot-page pack-picker / pack-preflight / pack-preflight-report：离屏打开安装概念窗口，
    /// 直接 RenderTo(App.ShotPath)。picker 用真实包库 + 逐行预检；预检两阶段用真实 PreflightPack
    /// （报告阶段用固定 fixture，保证可复现）。这些页不跑主窗口 ScheduleShot（否则会覆盖截图）。
    /// </summary>
    async Task ShotPackConceptWindowAsync(string path)
    {
        try
        {
            var state = AppState.Current;
            var inst = state.SelectedInstance;
            var profile = ProfileForShot(inst);
            if (App.ShotPage == "pack-picker")
            {
                var picker = new PackPickerWindow(inst?.Name ?? "sisyphus", profile)
                {
                    Owner = this,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                };
                picker.Show();
                if (inst is not null)
                    await picker.LoadAsync(new Packs(state.Launcher), inst, profile, state.Launcher.Root);
                else
                    picker.ShowDemo();
                // 让 ItemsControl 把最终行容器生成完再截图（预检回填走 Dispatcher）。
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                picker.UpdateLayout();
                picker.RenderTo(path);
                picker.Close();
            }
            else
            {
                var win = new PackPreflightWindow
                {
                    Owner = this,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                };
                win.Show();
                if (App.ShotPage == "pack-preflight-report")
                {
                    win.ShowReport(PackPreflightWindow.DemoReport("Test", inst?.Name ?? "sisyphus"));
                }
                else
                {
                    PackPreflight? preflight = null;
                    if (inst is not null)
                    {
                        var packs = new Packs(state.Launcher);
                        var cards = await packs.Catalog(AppContext.BaseDirectory, null, null, CancellationToken.None);
                        var card = cards.FirstOrDefault(c =>
                                       string.Equals(c.Id, "Test-0.1.0", StringComparison.OrdinalIgnoreCase))
                                   ?? cards.FirstOrDefault(c => c.Title.Contains("Test", StringComparison.OrdinalIgnoreCase))
                                   ?? cards.FirstOrDefault();
                        if (card is not null)
                            preflight = await packs.PreflightPack(inst, profile, card, null, CancellationToken.None);
                    }
                    win.ShowPreflight(preflight is not null
                        ? PackPreflightWindow.ToView(preflight, inst!.Name, profile)
                        : PackPreflightWindow.DemoPreflight("Test 0.1.0", inst?.Name ?? "sisyphus"));
                }
                win.UpdateLayout();
                win.RenderTo(path);
                win.Close();
            }
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(path + ".txt", ex.ToString()); } catch { /* 截图失败不炸 */ }
        }
        finally
        {
            if (App.ShotExit) Close();
        }
    }

    static string ProfileForShot(Core.Instance? inst)
    {
        if (inst is null) return "web";
        return inst.LastProfile ?? AppState.Current.Launcher.Profiles(inst).FirstOrDefault()?.Name ?? "web";
    }
    /// <summary>Render the live visual tree to a PNG, at the real device DPI.</summary>
    void ScheduleShot(string path)
    {
        var other = AppState.Current.Settings.Other;
        var ms = App.ShotPage is "market" or "relations" or "plugin-detail" || App.ShotPage.StartsWith("relations-sessions")
                ? Math.Max(other.ShotMarketDelayMs, 8000)
                : other.ShotDelayMs;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(ms),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
                var h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
                var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY,
                    PixelFormats.Pbgra32);
                rtb.Render(this);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(path);
                png.Save(fs);
                System.IO.File.WriteAllText(path + ".txt",
                    $"logical {ActualWidth}x{ActualHeight}\ndpi {dpi.DpiScaleX}x{dpi.DpiScaleY}\npixels {w}x{h}\n"
                    + $"host {(Host.Content as FrameworkElement)?.ActualWidth}x{(Host.Content as FrameworkElement)?.ActualHeight}\n"
                    + $"size {_lastSize}\n");
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText(path + ".txt", ex.ToString());
            }
            if (App.ShotExit) Close();
        };
        timer.Start();
    }

    UIElement Page(string name) => _pages.TryGetValue(name, out var cached)
        ? cached
        : _pages[name] = name switch
        {
            "launch" => new LaunchView(this),
            // 项目轴先隐藏（不做项目了）：管理页回到启动器本体「实例与插件」。
            "manage" => new ManageView(this),
            "bundles" => new ManageView(this),
            "relations" => new RelationsWebView(),
            "tasks" => new TaskView(this),
            "download" => new DownloadView(this),
            "settings" => new SettingsView(),
            "versions" => new VersionsView(this),
            "makepack" => new MakePackView(this),
            "instance" => new InstanceView(this),
            _ => new LaunchView(this),
        };

    public UIElement PageOf(string name) => Page(name);

    public void OpenSettingsApi()
    {
        Show("settings");
        TabSettings.IsChecked = true;
        if (Page("settings") is SettingsView sv) sv.OpenLayer("api");
    }

    /// <summary>Check a title tab so empty-state buttons can send the user to the next page.</summary>
    public void ShowTab(string name)
    {
        var tab = name switch
        {
            "manage" => TabManage,
            "tasks" => TabTasks,
            "download" => TabDownload,
            "settings" => TabSettings,
            _ => TabLaunch,
        };
        tab.IsChecked = true;
    }

    /// <summary>Open the download page on a community shelf. Used by 下载新插件.</summary>
    public void OpenDownloadShelf(string id)
    {
        Show("download");
        TabDownload.IsChecked = true;
        if (Page("download") is DownloadView dv) dv.OpenShelf(id);
    }

    /// <summary>Switch a top-level page. Restores the tab strip.</summary>
    public void Show(string name)
    {
        if (InnerMode.Visibility == Visibility.Visible)
            Fx.ChromeSwap(InnerMode, TopMode, forward: false);
        else
        {
            TopMode.Visibility = Visibility.Visible;
            InnerMode.Visibility = Visibility.Collapsed;
        }
        Swap(name);
    }

    /// <summary>Open an inner page: the tab strip is replaced by a back widget and a heading.</summary>
    /// <param name="backNav">True when returning — title slides the opposite way.</param>
    public void ShowInner(string name, string title, string back = "launch", bool backNav = false)
    {
        _back = back;
        var backLabel = PageNames.TryGetValue(back, out var label) ? label
            : back == "versions" ? "版本选择" : "启动";
        BackBtn.ToolTip = "返回" + backLabel;
        var forward = !backNav;
        var nextTitle = name == "instance" ? InstanceSetupTitle() : title;
        var wasInner = InnerMode.Visibility == Visibility.Visible;

        if (name == "instance")
            InnerBadge.Visibility = Visibility.Collapsed;
        else
        {
            var hostName = AppState.Current.SelectedInstance?.Name;
            InnerBadge.Text = hostName ?? "";
            InnerBadge.Visibility = string.IsNullOrEmpty(hostName) ? Visibility.Collapsed : Visibility.Visible;
        }

        if (wasInner)
        {
            // 两级内页和主页进二级同一套：旧顶栏先离开，新顶栏再进来。
            InnerLeavingTitle.Text = InnerTitle.Text;
            InnerLeavingBadge.Text = InnerBadge.Text;
            InnerLeavingBadge.Visibility = InnerBadge.Visibility;
            InnerTitle.Text = nextTitle;
            InnerLeaving.Visibility = Visibility.Visible;
            InnerLeaving.UpdateLayout();
            var travel = ChromeStage.ActualWidth > 1 ? ChromeStage.ActualWidth : ActualWidth;
            Fx.ChromeSwap(InnerLeaving, InnerLive, forward, travel: travel);
        }
        else
        {
            InnerTitle.Text = nextTitle;
            Fx.ChromeSwap(TopMode, InnerMode, forward: true);
        }

        Swap(name);
    }

    public void RefreshInnerTitle()
    {
        if (InnerMode.Visibility != Visibility.Visible) return;
        if (Host.Content is InstanceView)
            Fx.TitleSlide(InnerTitle, InstanceSetupTitle(), forward: true);
    }

    /// <summary>制作整合包取消/做完 → 回版本选择（还是内页，不回启动页）。</summary>
    public void BackToVersions() => ShowInner("versions", "版本选择", backNav: true);

    static string InstanceSetupTitle()
    {
        var n = AppState.Current.SelectedInstance?.Name;
        return string.IsNullOrEmpty(n) ? "版本设置" : "版本设置 - " + n;
    }

    public void OpenSettingsLaunch()
    {
        Show("settings");
        TabSettings.IsChecked = true;
        if (Page("settings") is SettingsView sv) sv.OpenLayer("launch");
    }

    int _swapGen;

    void Swap(string name)
    {
        _page = name;
        UiWatchdog.QuietFor(TimeSpan.FromSeconds(15));
        try
        {
            var page = Page(name);
            if (page is DependencyObject d)
                VisualTreeGuard.ThrowIfSelfHosted(d);
            var same = ReferenceEquals(Host.Content, page);
            var gen = ++_swapGen;

            // Order: leave → attach blank page → shell SizeTo + PageIn in parallel both ways.
            void Arrive()
            {
                if (gen != _swapGen) return;
                if (!ReferenceEquals(Host.Content, page))
                {
                    Host.Content = page;
                    Fx.PrepareEnter(page);
                }
                AttachStageRail(page, animate: false);

                if (same)
                {
                    if (page is IRefreshable r0) RefreshAfterEntrance(page, r0, 0);
                    return;
                }

                var revealed = false;

                void PlayEnter(bool resized, bool refresh, bool withShell)
                {
                    if (gen != _swapGen) return;
                    Fx.PrepareEnter(page);
                    Fx.BusyFor(resized ? 900 : 700);
                    Fx.PageIn(page, withShell: withShell);
                    if (refresh && page is IRefreshable r)
                        RefreshAfterEntrance(page, r, resized ? 820 : 600);
                }

                var resizing = ApplyPageSize(name, animate: true,
                    onDone: () =>
                    {
                        if (gen != _swapGen) return;
                        if (revealed)
                        {
                            // Probe after morph — OnShown mid-SizeTo was a steady hitch.
                            if (page is IRefreshable r)
                                RefreshAfterEntrance(page, r, 200);
                            return;
                        }
                        PlayEnter(resized: true, refresh: true, withShell: false);
                    },
                    onReveal: () =>
                    {
                        if (gen != _swapGen) return;
                        revealed = true;
                        // Next dispatcher turn: SizeTo clock is already running.
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (gen != _swapGen) return;
                            PlayEnter(resized: true, refresh: false, withShell: true);
                        }), DispatcherPriority.Input);
                    });
                if (!resizing) PlayEnter(resized: false, refresh: true, withShell: false);
            }

            if (same)
            {
                AttachStageRail(page, animate: false);
                ApplyPageSize(name, animate: true);
                if (page is IRefreshable r) RefreshAfterEntrance(page, r, 0);
                return;
            }

            var leaving = Host.Content as UIElement;
            if (leaving is null || !Fx.Animated)
            {
                Arrive();
                return;
            }

            Fx.BusyFor(360);
            Fx.PageOut(leaving, Arrive);
        }
        catch (Exception ex)
        {
            App.ReportPage(ex);
        }
    }

    void RefreshAfterEntrance(UIElement page, IRefreshable refresh, int delayMs)
    {
        void Run()
        {
            if (!ReferenceEquals(Host.Content, page)) return;
            try { refresh.OnShown(); }
            catch (Exception ex) { App.ReportPage(ex); }
        }

        if (delayMs <= 0)
        {
            Dispatcher.BeginInvoke(new Action(Run), DispatcherPriority.Background);
            return;
        }
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Fx.Animated ? delayMs : 40),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Run();
        };
        timer.Start();
    }

    // ------------------------------------------------------------------ 舞台侧栏

    const double RailW = 236;
    bool _railOpen = true;

    /// <summary>
    /// 侧栏容器归舞台（D35）：页面带来自己的视角，挂接 = 把视角灌进容器；
    /// 其余页面容器向左滑出、内容区连续重排。页面只有"重新组合"，没有"换了一个"。
    /// </summary>
    void AttachStageRail(UIElement page, bool animate)
    {
        switch (page)
        {
            // 项目侧栏随项目轴一起隐藏；管理页现在是启动器本体，不再挂项目栏。
            case RelationsWebView rw:
                rw.AttachRail(StageRailList);
                rw.GoBack = () => Show("manage");
                ShowRail(animate);
                break;
            default:
                HideRail(animate);
                break;
        }
    }

    void ShowRail(bool animate = true)
    {
        StageRailList.ShowCollapse = true;
        if (!_railOpen)
        {
            // 页面换了但用户收着侧栏：保持收起，把手留在原位
            SnapRail(open: false, edge: true);
            return;
        }
        if (!animate || !Fx.Animated)
        {
            SnapRail(open: true, edge: false);
            return;
        }
        SlideRail(open: true, edgeOnClose: false);
    }

    void HideRail(bool animate = true)
    {
        if (StageRail.Visibility != Visibility.Visible && RailColumn.Width.Value < 1)
        {
            SnapRail(open: false, edge: false);
            return;
        }
        if (!animate || !Fx.Animated)
        {
            SnapRail(open: false, edge: false);
            return;
        }
        SlideRail(open: false, edgeOnClose: false);
    }

    void ToggleRail()
    {
        _railOpen = !_railOpen;
        if (!Fx.Animated)
        {
            SnapRail(_railOpen, edge: !_railOpen);
            return;
        }
        SlideRail(_railOpen, edgeOnClose: true);
    }

    const double EdgeW = 20;
    int _railGen;

    void SetRailColumn(double px) => RailColumn.Width = new GridLength(Math.Max(0, px));

    double RailColumnPx =>
        RailColumn.Width.GridUnitType == GridUnitType.Pixel ? RailColumn.Width.Value : 0;

    /// <summary>列宽立刻到位（只排一次）。侧栏和内容只平移，不再逐帧改宽度。</summary>
    void SlideRail(bool open, bool edgeOnClose)
    {
        var gen = ++_railGen;
        if (open && RailColumnPx > 0 && RailColumnPx < RailW - 1)
            SetRailColumn(0);

        var currentCol = RailColumnPx;
        var targetCol = open ? RailW : 0;
        var delta = targetCol - currentCol;

        StageRail.Width = RailW;
        StageRail.Visibility = Visibility.Visible;
        if (open) RailEdge.Visibility = Visibility.Collapsed;

        var hostNow = HostShift.X;
        var railNow = StageRailShift.X;
        SetRailColumn(targetCol);
        HostShift.BeginAnimation(TranslateTransform.XProperty, null);
        HostShift.X = hostNow - delta;
        StageRailShift.BeginAnimation(TranslateTransform.XProperty, null);
        StageRailShift.X = railNow;

        const int ms = 260;
        Fx.Slide(HostShift, 0, ms);
        Fx.Slide(StageRailShift, open ? 0 : -RailW, ms);

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = Fx.Span(ms) + TimeSpan.FromMilliseconds(40),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (gen != _railGen) return;
            SnapRail(open, edge: !open && edgeOnClose);
        };
        timer.Start();
    }

    void SnapRail(bool open, bool edge)
    {
        _railGen++;
        StageRail.Width = RailW;
        StageRailShift.BeginAnimation(TranslateTransform.XProperty, null);
        HostShift.BeginAnimation(TranslateTransform.XProperty, null);
        HostShift.X = 0;
        if (open)
        {
            RailEdge.Visibility = Visibility.Collapsed;
            StageRail.Visibility = Visibility.Visible;
            StageRailShift.X = 0;
            SetRailColumn(RailW);
            return;
        }
        StageRailShift.X = -RailW;
        StageRail.Visibility = Visibility.Collapsed;
        RailEdge.Visibility = edge ? Visibility.Visible : Visibility.Collapsed;
        SetRailColumn(edge ? EdgeW : 0);
    }

    void RailEdge_Click(object sender, RoutedEventArgs e)
    {
        if (!_railOpen) ToggleRail();
    }

    /// <summary>
    /// 关系页：舞台上的另一种组合。标签条不动、侧栏不动、窗口模式不变——
    /// 只有内容区把卡片换成图；返回入口在内容区头部，不再整块换 Chrome。
    /// </summary>
    public void ShowRelations()
    {
        if (_page == "relations") return;
        Swap("relations");
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        // 内页里的返回：制作整合包 → 版本选择（还是内页）。
        if (_back == "versions")
        {
            ShowInner("versions", "版本选择", "launch", backNav: true);
            return;
        }
        // Returning from an inner page re-checks its owning tab, which fires Tab_Checked.
        var tab = _back switch
        {
            "manage" => TabManage,
            "tasks" => TabTasks,
            "download" => TabDownload,
            "settings" => TabSettings,
            _ => TabLaunch,
        };
        if (tab.IsChecked == true) Show(_back);
        else tab.IsChecked = true;
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        MoveTabPill(tag);
        if (Host is null) return;
        Show(tag);
    }

    string CurrentTab() =>
        TabManage.IsChecked == true ? "manage"
        : TabTasks.IsChecked == true ? "tasks"
        : TabDownload.IsChecked == true ? "download"
        : TabSettings.IsChecked == true ? "settings"
        : "launch";

    void MoveTabPill(string tag)
    {
        if (TabPill is null || TabStrip is null) return;
        var tab = tag switch
        {
            "manage" => TabManage,
            "tasks" => TabTasks,
            "download" => TabDownload,
            "settings" => TabSettings,
            _ => TabLaunch,
        };
        if (!Fx.TryLayoutSlot(TabStrip, tab, out var at, out var size) || size.Width <= 1) return;
        if (!_pillReady || !Fx.Animated)
        {
            SnapTabPill(size.Width, at.X);
            _pillReady = true;
            return;
        }
        // 宽度只在点击时定一次。过程里用 ScaleX 从当前视觉宽度收到目标，
        // 和位移一起走渲染线程，不再每帧重排。
        var visualW = TabPill.Width * TabPillScale.ScaleX;
        if (visualW < 1 || double.IsNaN(visualW)) visualW = size.Width;
        TabPill.BeginAnimation(WidthProperty, null);
        TabPill.Width = size.Width;
        var from = size.Width <= 1 ? 1 : visualW / size.Width;
        TabPillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TabPillScale.ScaleX = from;
        Fx.ScaleXTo(TabPillScale, 1);
        Fx.SpringSlide(TabPillShift, at.X);
    }

    void SnapTabPill(double width, double x)
    {
        TabPill.BeginAnimation(WidthProperty, null);
        TabPill.Width = width;
        TabPillScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        TabPillScale.ScaleX = 1;
        TabPillShift.BeginAnimation(TranslateTransform.XProperty, null);
        TabPillShift.X = x;
    }

    bool _pillReady;

    /// <summary>
    /// A corner notice for work whose result is not visible in this window — a terminal
    /// that opened somewhere else, or a subprocess that failed. Never for a change the
    /// user can already see.
    /// </summary>
    /// <summary>
    /// PCL 式更新检查：启动后后台查 latest.json，有新版就提示，用户自己决定下载；
    /// 设置里勾了「自动下载」才会直接下载（仍然重启才替换）。
    /// </summary>
    public async void ProbePadUpdate()
    {
        var launcher = Core.AppState.Current.Launcher;
        if (!Core.PadUpdate.Enabled(launcher)) return;
        Core.PadUpdateInfo? info = null;
        try { info = await Task.Run(() => Core.PadUpdate.Check(launcher, CancellationToken.None)); }
        catch { return; }
        if (info is null || !Core.PadUpdate.IsNewer(info.Version, Core.PadUpdate.CurrentVersion())) return;
        if (Core.AppState.Current.Settings.Other.UpdateAutoDownload)
        {
            try
            {
                await Core.PadUpdate.Download(launcher, info, null, CancellationToken.None);
                Toast($"PAD {info.Version} 已下载，重启后自动替换");
                return;
            }
            catch { /* 落回手动提示 */ }
        }
        Toast($"PAD 有新版 {info.Version}：设置 → 升级 里可以下载");
    }

    public void Toast(string text, bool bad = false)
    {
        if (bad)
        {
            if (App.ShotPath is null)
                MessageBox.Show(this, text, "PAD", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        while (ToastLayer.Children.Count >= Math.Max(1, AppState.Current.Settings.Ui.ToastMax))
            ToastLayer.Children.RemoveAt(0);

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(new Border
        {
            Style = (Style)FindResource("Pip"),
            Background = (Brush)FindResource(bad ? "Danger" : "Accent"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 9, 0),
        });
        body.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = (Brush)FindResource("ChromeInk"),
            FontSize = 12,
            MaxWidth = 340,
            TextWrapping = TextWrapping.Wrap,
        });

        var shift = new TranslateTransform();
        var card = new Border
        {
            Style = (Style)FindResource("Toast"),
            Margin = new Thickness(0, 8, 0, 0),
            Child = body,
            RenderTransform = shift,
            IsHitTestVisible = false, // toast 只提示，不挡下面；进度卡单独可点
        };
        ToastLayer.Children.Add(card);

        if (Fx.Animated)
        {
            var ease = new PowerEase { Power = 4.6, EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180))) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = ease });
        }

        var life = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(
                Math.Clamp(AppState.Current.Settings.Ui.ToastMs, 1200, 10000)),
        };
        life.Tick += (_, _) =>
        {
            life.Stop();
            if (!Fx.Animated) { ToastLayer.Children.Remove(card); return; }
            var fade = Fx.ToastOut(shift);
            fade.Completed += (_, _) => ToastLayer.Children.Remove(card);
            card.BeginAnimation(OpacityProperty, fade);
        };
        life.Start();
    }

    Border? _progressCard;
    TextBlock? _progressTitle;
    TextBlock? _progressLine;
    TextBlock? _progressPct;
    Border? _progressTrack;
    Border? _progressFill;
    double? _progressFraction;
    JobRecord? _progressJob;
    bool _progressCollapsed;
    /// <summary>进度到 100% 后自动收卡片（扫描类调用只推进度、不会调 ProgressDone）。</summary>
    readonly System.Windows.Threading.DispatcherTimer _progressIdle = new();
    StackPanel? _progressDetail;
    StackPanel? _progressActions;

    /// <summary>
    /// 右下角长方形进度卡：logo + 标题 + 百分比 + 一行「已复制 x/y MB · 速度 · 剩余」+ 进度条。
    /// 右上角「-」收起、」×」隐藏；任务还在跑时可在这里直接取消/删除；点卡片跳下载任务。
    /// </summary>
    public void Progress(string text, double? fraction = null, string? title = null, JobRecord? job = null)
    {
        if (App.ShotPath is not null) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Progress(text, fraction, title, job)));
            return;
        }
        if (_progressCard is null) BuildProgressCard();
        if (job is not null) _progressJob = job;
        _progressLine!.Text = text;
        if (!string.IsNullOrWhiteSpace(title)) _progressTitle!.Text = title;
        _progressFraction = fraction;
        if (fraction is { } f)
        {
            _progressPct!.Text = $"{Math.Clamp(f, 0, 1) * 100:0}%";
            _progressPct.Visibility = Visibility.Visible;
            _progressFill!.Visibility = Visibility.Visible;
            UpdateProgressFill();
        }
        else
        {
            _progressPct!.Text = "";
            _progressPct.Visibility = Visibility.Collapsed;
            _progressFill!.Visibility = Visibility.Collapsed;
        }
        if (_progressActions is not null)
            _progressActions.Visibility = _progressJob is { CanCancel: true } && !_progressCollapsed
                ? Visibility.Visible
                : Visibility.Collapsed;

        // 扫描这类活儿只推进度、不负责收卡片；到 100% 后自己收掉，别一直挂在右下角。
        if (fraction is { } finished && finished >= 1)
        {
            _progressIdle.Stop();
            _progressIdle.Interval = TimeSpan.FromMilliseconds(1500);
            _progressIdle.Start();
        }
        else
        {
            _progressIdle.Stop();
        }
    }

    void BuildProgressCard()
    {
        _progressTitle = new TextBlock
        {
            Foreground = (Brush)FindResource("TextPrimary"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 0, 0),
        };
        _progressPct = new TextBlock
        {
            Foreground = (Brush)FindResource("AccentText"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 8, 0),
        };
        var logo = new Image
        {
            Width = 26,
            Height = 26,
            Stretch = Stretch.Uniform,
            Source = Skin.LoadBitmap(null, new Uri("pack://application:,,,/Assets/pad-logo.png")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var minus = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = "–",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "收起",
        };
        var close = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = "✕",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 10,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "隐藏（任务继续，在下载任务里看）",
        };
        minus.Click += (_, _) =>
        {
            _progressCollapsed = !_progressCollapsed;
            if (_progressDetail is not null)
                _progressDetail.Visibility = _progressCollapsed ? Visibility.Collapsed : Visibility.Visible;
            if (_progressActions is not null)
                _progressActions.Visibility = _progressCollapsed || _progressJob is not { CanCancel: true }
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            minus.Content = _progressCollapsed ? "+" : "–";
            minus.ToolTip = _progressCollapsed ? "展开" : "收起";
        };
        close.Click += (_, _) =>
        {
            Toast("进度卡已隐藏，可在下载任务里看");
            ProgressDone(0);
        };
        var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(logo);
        Grid.SetColumn(_progressTitle, 1);
        head.Children.Add(_progressTitle);
        Grid.SetColumn(_progressPct, 2);
        head.Children.Add(_progressPct);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(minus);
        controls.Children.Add(close);
        Grid.SetColumn(controls, 3);
        head.Children.Add(controls);

        _progressLine = new TextBlock
        {
            Foreground = (Brush)FindResource("TextMuted"),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Margin = new Thickness(0, 0, 0, 8),
        };
        _progressTrack = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)FindResource("LineDivider"),
        };
        _progressFill = new Border
        {
            Height = 5,
            CornerRadius = new CornerRadius(3),
            Background = (Brush)FindResource("Accent"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = new ScaleTransform(0, 1),
        };
        var track = new Grid();
        track.Children.Add(_progressTrack);
        track.Children.Add(_progressFill);
        _progressTrack.SizeChanged += (_, _) => UpdateProgressFill();

        var cancel = new Button
        {
            Style = (Style)FindResource("BtnDanger"),
            Content = "取消",
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11.5,
        };
        cancel.Click += (_, _) =>
        {
            if (_progressJob is null) return;
            try
            {
                AppState.Current.Launcher.CancelJob(_progressJob.Id);
                Toast("已取消 " + _progressJob.Title);
            }
            catch (Exception ex) { Toast(PadError.Describe(ex), bad: true); }
            ProgressDone(300);
        };
        var delete = new Button
        {
            Style = (Style)FindResource("BtnSm"),
            Content = "删除",
            Height = 24,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11.5,
        };
        delete.Click += (_, _) =>
        {
            if (_progressJob is null) return;
            try
            {
                AppState.Current.Launcher.DeleteJob(_progressJob.Id);
                Toast("已删除 " + _progressJob.Title);
            }
            catch (Exception ex) { Toast(PadError.Describe(ex), bad: true); }
            ProgressDone(300);
        };
        var pause = new Button
        {
            Style = (Style)FindResource("BtnSm"),
            Content = "暂停",
            Height = 24,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 11.5,
        };
        pause.Click += (_, _) =>
        {
            if (_progressJob is null) return;
            try
            {
                var paused = AppState.Current.Launcher.ListJobs().FirstOrDefault(j => j.Id == _progressJob.Id)?.Paused == true;
                if (paused)
                {
                    AppState.Current.Launcher.ResumeJob(_progressJob.Id);
                    pause.Content = "暂停";
                    Toast("已继续 " + _progressJob.Title);
                }
                else
                {
                    AppState.Current.Launcher.PauseJob(_progressJob.Id);
                    pause.Content = "继续";
                    Toast("已暂停 " + _progressJob.Title);
                }
            }
            catch (Exception ex) { Toast(PadError.Describe(ex), bad: true); }
        };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        actions.PreviewMouseLeftButtonUp += (_, e) => e.Handled = true;
        actions.Children.Add(pause);
        actions.Children.Add(cancel);
        actions.Children.Add(delete);
        _progressActions = actions;

        _progressDetail = new StackPanel();
        _progressDetail.Children.Add(_progressLine);
        _progressDetail.Children.Add(track);

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(_progressDetail);
        body.Children.Add(actions);
        _progressCard = new Border
        {
            Width = 400,
            Background = (Brush)FindResource("BgCard"),
            BorderBrush = (Brush)FindResource("LineBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 10, 12),
            Margin = new Thickness(0, 8, 0, 0),
            Child = body,
            Cursor = Cursors.Hand,
            ToolTip = "打开下载任务",
        };
        var self = _progressCard;
        self.MouseLeftButtonUp += (_, _) => OpenDownloadTasks();
        // 用局部 self：ProgressDone 会把 _progressCard 置空，鼠标离开时才不会 NRE（02:49:27 崩溃）。
        self.MouseEnter += (_, _) => self.Background = (Brush)FindResource("AccentWash");
        self.MouseLeave += (_, _) => self.Background = (Brush)FindResource("BgCard");
        ToastLayer.Children.Add(_progressCard);
        if (Fx.Animated)
        {
            var shift = new TranslateTransform();
            _progressCard.RenderTransform = shift;
            _progressCard.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(160)))
                {
                    EasingFunction = new PowerEase { Power = 4.6, EasingMode = EasingMode.EaseOut },
                });
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(16, 0, new Duration(TimeSpan.FromMilliseconds(220)))
                {
                    EasingFunction = new PowerEase { Power = 4.6, EasingMode = EasingMode.EaseOut },
                });
        }
    }

    void UpdateProgressFill()
    {
        if (_progressFill is null || _progressTrack is null) return;
        if (_progressFill.RenderTransform is not ScaleTransform scale)
            return;
        var frac = Math.Clamp(_progressFraction ?? 0, 0, 1);
        if (!Fx.Animated)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = frac;
            return;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(frac, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>点进度卡：跳到下载页的「下载任务」（同一个 jobs.json）。</summary>
    public void OpenDownloadTasks()
    {
        Show("download");
        TabDownload.IsChecked = true;
        if (Page("download") is DownloadView dv) dv.OpenShelf("dltasks");
    }

    public void ProgressDone(int delayMs = 900)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ProgressDone(delayMs)));
            return;
        }
        _progressIdle.Stop();
        var card = _progressCard;
        _progressCard = null;
        _progressTitle = null;
        _progressLine = null;
        _progressPct = null;
        _progressTrack = null;
        _progressFill = null;
        _progressFraction = null;
        _progressJob = null;
        _progressCollapsed = false;
        _progressDetail = null;
        _progressActions = null;
        if (card is null) return;
        var life = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(0, delayMs)),
        };
        life.Tick += (_, _) =>
        {
            life.Stop();
            ToastLayer.Children.Remove(card);
        };
        life.Start();
    }

    /// <summary>PCL MyMsgText: in-window card. Warn puts focus on 取消 so Enter does not confirm delete.</summary>
    public bool Ask(string title, string body, bool warn = false, string? previewPath = null,
        string? okText = null, string? cancelText = null) =>
        ShowMsg(title, body, input: false, seed: "", warn, previewPath, okText, cancelText) is not null;

    public enum HintChoice { Ok, Cancel, Settings }

    /// <summary>托盘提示卡：好的 / 手滑了 / 点我跳转（设置）三态，带「不再提醒」复选框。</summary>
    public HintChoice AskTrayHint(string title, string body, string linkText, string checkText,
        out bool checkedDismiss)
    {
        _msgChoice = 0;
        ShowMsg(title, body, input: false, seed: "", warn: false, previewPath: null,
            okText: "好的", cancelText: "我只是手滑了.....", checkText: checkText,
            linkText: linkText,
            linkHandler: () => { EndMsg(true); _msgChoice = 3; });
        checkedDismiss = _msgCheckResult;
        return _msgChoice switch
        {
            1 => HintChoice.Ok,
            3 => HintChoice.Settings,
            _ => HintChoice.Cancel,
        };
    }

    /// <summary>PCL MyMsgInput: in-window card with a text box.</summary>
    public string? AskInput(string title, string? body = null, string seed = "", bool warn = false) =>
        ShowMsg(title, body ?? "", input: true, seed, warn);

    string? ShowMsg(string title, string body, bool input, string seed, bool warn, string? previewPath = null,
        string? okText = null, string? cancelText = null, string? checkText = null, bool singleButton = false,
        string? linkText = null, Action? linkHandler = null)
    {
        if (_msgFrame is not null) return null;
        MsgTitle.Text = title;
        MsgBody.Text = body;
        MsgBody.Visibility = body.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        MsgInput.Text = seed;
        MsgInput.Visibility = input ? Visibility.Visible : Visibility.Collapsed;
        var preview = !string.IsNullOrWhiteSpace(previewPath) && File.Exists(previewPath);
        MsgPreviewBox.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        MsgPreview.Source = preview ? Skin.LoadBitmap(previewPath) : null;
        MsgTitle.Foreground = (Brush)FindResource(warn ? "DangerText" : "AccentText");
        MsgLine.Fill = (Brush)FindResource(warn ? "Danger" : "Accent");
        MsgDim.Background = warn
            ? new SolidColorBrush(Color.FromArgb(0x99, 0x5A, 0x18, 0x14))
            : new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        MsgOk.Content = string.IsNullOrWhiteSpace(okText) ? "确定" : okText;
        MsgCancel.Content = string.IsNullOrWhiteSpace(cancelText) ? "取消" : cancelText;
        MsgCheck.Content = string.IsNullOrWhiteSpace(checkText) ? "不再提醒" : checkText;
        MsgCheck.IsChecked = false;
        MsgCheck.Visibility = checkText is null ? Visibility.Collapsed : Visibility.Visible;
        MsgCancel.Visibility = singleButton ? Visibility.Collapsed : Visibility.Visible;
        _msgLinkHandler = linkHandler;
        MsgLinkButton.Inlines.Clear();
        if (!string.IsNullOrWhiteSpace(linkText)) MsgLinkButton.Inlines.Add(new Run(linkText));
        MsgLink.Visibility = string.IsNullOrWhiteSpace(linkText) ? Visibility.Collapsed : Visibility.Visible;
        MsgOk.Style = (Style)FindResource(warn ? "BtnDanger" : "BtnPrimary");
        // 输入框里不能让 Enter 直接提交：中文输入法组合中的回车会先把旧文本交出去（改名看起来像没生效）。\r\n        MsgOk.IsDefault = !warn && !input;
        MsgCancel.IsDefault = warn;
        _msgAccepted = false;
        _msgCheckResult = false;
        MsgLayer.Visibility = Visibility.Visible;
        MsgCard.Opacity = 1;
        Fx.PulseIn(MsgCard, true);
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (MsgLayer.Visibility != Visibility.Visible) return;
            if (input)
            {
                MsgInput.Focus();
                MsgInput.SelectAll();
            }
            else if (warn)
                MsgCancel.Focus();
            else
                MsgOk.Focus();
        }, DispatcherPriority.Input);
        _msgFrame = new DispatcherFrame();
        Dispatcher.PushFrame(_msgFrame);
        _msgFrame = null;
        MsgLayer.Visibility = Visibility.Collapsed;
        MsgPreview.Source = null;
        MsgPreviewBox.Visibility = Visibility.Collapsed;
        MsgOk.IsDefault = false;
        MsgCancel.IsDefault = false;
        MsgOk.Content = "确定";
        MsgCancel.Content = "取消";
        MsgCancel.Visibility = Visibility.Visible;
        MsgCheck.Visibility = Visibility.Collapsed;
        MsgCheck.IsChecked = false;
        MsgLink.Visibility = Visibility.Collapsed;
        MsgLinkButton.Inlines.Clear();
        _msgLinkHandler = null;
        MsgCard.Opacity = 1;
        MsgCard.RenderTransform = null;
        if (!_msgAccepted) return null;
        return input ? MsgInput.Text.Trim() : "";
    }

    /// <summary>输入框里的 Enter/Esc：输入法正在组合时让输入法先吃，组合结束后才提交。</summary>
    void MsgInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_msgFrame is null) return;
        if (e.Key == Key.ImeProcessed) return;
        if (e.Key == Key.Enter) { e.Handled = true; EndMsg(true); }
        else if (e.Key == Key.Escape) { e.Handled = true; EndMsg(false); }
    }
    void MsgOk_Click(object sender, RoutedEventArgs e) => EndMsg(true);

    void MsgCancel_Click(object sender, RoutedEventArgs e) => EndMsg(false);

    void EndMsg(bool accepted)
    {
        if (_msgFrame is null) return;
        _msgAccepted = accepted;
        _msgChoice = accepted ? 1 : 2;
        _msgCheckResult = MsgCheck.IsChecked == true;
        _msgFrame.Continue = false;
    }

    void MsgLink_Click(object sender, RoutedEventArgs e)
    {
        if (_msgFrame is null) return;
        var handler = _msgLinkHandler;
        EndMsg(true);
        _msgChoice = 3;
        handler?.Invoke();
    }

    bool _msgCheckResult;
    int _msgChoice;
    Action? _msgLinkHandler;

    /// <summary>readme.md overlay on the main window. Headings render; no second HWND.</summary>
    public void ShowMd(string title, string markdown)
    {
        MdTitle.Text = title;
        MdDoc.Document = Md.Document(markdown);
        ApplyMdPageWidth();
        MdLayer.Visibility = Visibility.Visible;
        MdCard.Opacity = 1;
        Fx.PulseIn(MdCard, true);
        _ = Dispatcher.InvokeAsync(ApplyMdPageWidth, DispatcherPriority.Loaded);
    }

    public void HideMd()
    {
        MdLayer.Visibility = Visibility.Collapsed;
        MdDoc.Document = new FlowDocument();
        MdCard.Opacity = 1;
        MdCard.RenderTransform = null;
    }

    void MdDim_Down(object sender, MouseButtonEventArgs e) => HideMd();

    void MdClose_Click(object sender, RoutedEventArgs e) => HideMd();

    void MdDoc_Size(object sender, SizeChangedEventArgs e) => ApplyMdPageWidth();

    void ApplyMdPageWidth()
    {
        if (MdDoc.Document is not FlowDocument doc) return;
        var w = MdDoc.ActualWidth;
        if (w > 40) doc.PageWidth = w - 16;
    }

    static void OnMdLink(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* ignore dead links */ }
        e.Handled = true;
    }

    /// <summary>Launch failed: error plus crash-analyze / open-logs, like PCL's fail dialog.</summary>
    public void LaunchFailed(string text, Instance? inst)
    {
        if (App.ShotPath is not null) return;
        if (inst is null)
        {
            MessageBox.Show(this, text, "PAD", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var analyze = new Button { Content = "崩溃分析", Width = 90 };
        var logs = new Button { Content = "打开日志", Width = 90, Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "关闭", Width = 70, IsDefault = true, IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0) };
        var dlg = new Window
        {
            Title = "PAD",
            Width = 520,
            Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock
                    {
                        Text = text,
                        FontFamily = (FontFamily)FindResource("FontUI"),
                        FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 460,
                        MaxHeight = 160,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 16, 0, 0),
                        Children = { analyze, logs, close },
                    },
                },
            },
        };
        analyze.Click += (_, _) =>
        {
            try
            {
                var report = CrashAnalyzer.Analyze(AppState.Current.Launcher, inst.Id).Render();
                MessageBox.Show(this, report.Length > 2000 ? report[..2000] : report, "崩溃分析",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { Toast(PadError.Describe(ex), bad: true); }
            dlg.Close();
        };
        logs.Click += (_, _) =>
        {
            Reveal.Path(AppState.Current.Launcher.LogsDir(inst.Id));
            dlg.Close();
        };
        close.Click += (_, _) => dlg.Close();
        dlg.ShowDialog();
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        var packs = LaunchPolicy.PackPaths(e.Data);
        if (packs.Count > 0)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        // PackForge 包走 compat 引擎，和原生 .pack.zip 的投影路径分开。
        if (LaunchPolicy.DspackPaths(e.Data).Count > 0)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        var images = LaunchPolicy.ImagePaths(e.Data);
        if (images.Count > 0 && _page == "instance"
            && _pages.TryGetValue("instance", out var page) && page is InstanceView iv && iv.CanTakeLogoDrop)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var dspacks = LaunchPolicy.DspackPaths(e.Data);
        if (dspacks.Count > 0)
        {
            _ = TakeDspacks(dspacks);
            return;
        }
        var packs = LaunchPolicy.PackPaths(e.Data);
        if (packs.Count > 0)
        {
            _ = TakePacks(packs);
            return;
        }
        var images = LaunchPolicy.ImagePaths(e.Data);
        if (images.Count > 0 && _page == "instance"
            && _pages.TryGetValue("instance", out var page) && page is InstanceView iv)
            iv.OfferDroppedLogo(images[0]);
    }

    bool _dspackImporting;

    /// <summary>
    /// Drag-dropped PackForge <c>.dspack</c>: fresh isolated instance first, then the
    /// pack-runtime compat engine lands overrides/home/bundles (and pinned deps).
    /// </summary>
    public async Task TakeDspacks(IReadOnlyList<string> paths)
    {
        if (_dspackImporting)
        {
            Toast("上一个整合包还在装，等它装完再拖", bad: true);
            return;
        }
        _dspackImporting = true;
        try
        {
            var state = AppState.Current;
            var packs = new Packs(state.Launcher);
            foreach (var path in paths)
            {
                try
                {
                    Toast("正在装 " + System.IO.Path.GetFileName(path));
                    var result = await PacksForge.ImportAuto(state.Launcher, packs, path,
                        deps: true, log: null, CancellationToken.None);
                    state.Reload();
                    _ = state.ReloadPackCatalog();
                    ShowInner("instance", "版本设置");
                    Toast($"装好了：{result.Name}（DSH {result.Release}）");
                }
                catch (Exception ex)
                {
                    Toast(PadError.Describe(ex), bad: true);
                }
            }
        }
        finally
        {
            _dspackImporting = false;
        }
    }

    public async Task TakePacks(IReadOnlyList<string> paths)
    {
        var state = AppState.Current;
        var packs = new Packs(state.Launcher);
        var missing = packs.Unavailable();
        if (missing is not null)
        {
            Toast($"{missing.Reason}。{missing.Detail}", bad: true);
            return;
        }

        var log = new Progress<string>(_ => { });
        var imported = false;
        try
        {
            foreach (var path in paths)
            {
                if (LauncherPack.Contains(path))
                {
                    await OpenLauncherPackAsync(path);
                    continue;
                }

                // v3 包新建实例。其余格式也走 import，不改正在看的实例。
                var spec = PackSpec.Load(path);
                if (spec.Valid && spec.Schema == PackSpec.SchemaV3)
                {
                    var win = new PackImportWindow(state.Launcher, path, spec, createNew: true) { Owner = this };
                    if (win.ShowDialog() == true && win.Installed is not null)
                    {
                        imported = true;
                        state.Reload();
                        Toast($"已导入 {spec.Name} {spec.Version}：{win.Summary}");
                    }
                    continue;
                }

                Toast("正在装 " + System.IO.Path.GetFileName(path));
                await packs.Import(path, log, CancellationToken.None);
                imported = true;
            }
            state.Reload();
            if (imported) Toast("装好了");
        }
        catch (Exception ex)
        {
            Toast(PadError.Describe(ex), bad: true);
        }
    }

    /// <summary>
    /// 带启动器的整合包：解到用户选的空文件夹。找到 exe 就问要不要换用它。
    /// 锁是按启动器根分的，新目录和当前 PAD 不抢同一把锁。
    /// </summary>
    public async Task OpenLauncherPackAsync(string zipPath)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择一个空文件夹，整合包会解到这里",
        };
        if (dlg.ShowDialog(this) != true) return;
        var dest = dlg.FolderName;
        if (!LauncherPack.IsEmpty(dest))
        {
            Toast("选一个空文件夹", bad: true);
            return;
        }
        Progress("正在解开…", null, "整合包");
        try
        {
            await Task.Run(() => LauncherPack.Extract(zipPath, dest));
        }
        catch (Exception ex)
        {
            ProgressDone(0);
            Toast(PadError.Describe(ex), bad: true);
            return;
        }
        ProgressDone(0);
        var exe = LauncherPack.FindExe(dest);
        if (exe is null)
        {
            Toast("解开了，里面没有启动器");
            Reveal.Path(dest);
            return;
        }
        if (!Ask("换用包里的启动器？", "即将打开：" + exe, okText: "换用", cancelText: "留在这里"))
        {
            Toast("已解开");
            Reveal.Path(dest);
            return;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) });
        Application.Current.Shutdown();
    }

    async void PickupSidecarPacks()
    {
        var state = AppState.Current;
        try
        {
            Packs.CollectSidecar(AppContext.BaseDirectory, state.Launcher.Root);
            if (!System.IO.Directory.Exists(state.Launcher.Root)) return;
            var any = System.IO.Directory.EnumerateFiles(state.Launcher.Root).Any(LaunchPolicy.IsSidecarZip);
            if (!any) return;
            var packs = new Packs(state.Launcher);
            // 先留一份进插件包库：自动导入会删掉根目录的 zip，库里这份让「插件包」
            // 列表还能看到它、可再建实例（PCL 的 modpack 目录语义）。
            try
            {
                foreach (var zip in System.IO.Directory.EnumerateFiles(state.Launcher.Root)
                             .Where(LaunchPolicy.IsSidecarZip))
                    packs.CollectToLibrary(zip);
            }
            catch { /* 库拷贝失败不挡自动导入 */ }
            var fail = packs.Unavailable();
            if (fail is not null)
            {
                Toast(fail.Reason + "\n" + fail.Detail, bad: true);
                return;
            }
            await packs.ScanDrop(null, CancellationToken.None);
            state.Reload();
            _ = state.ReloadPackCatalog();
        }
        catch (Exception ex)
        {
            Toast(PadError.Describe(ex), bad: true);
        }
    }

    /// <summary>
    /// 启动对账（D56）然后列出还没收编的 home（D114）。对账能确定才改指；清单勾选才收编，不自动收。
    /// </summary>
    void AutoReconcileHome()
    {
        var state = AppState.Current;
        var progress = new Progress<Discover.DiscoverStep>(s => Progress(s.Text, s.Fraction, "扫描"));
        Progress("正在扫描本机已有的 DSH…", 0, "扫描");
        Task.Run(() =>
        {
            string? movedTo = null;
            Inventory? inventory = null;
            try
            {
                try { inventory = Discover.Run(state.Launcher, [], CancellationToken.None); }
                catch { /* 扫不出来就只按 pad.json + 进程 DSH_HOME 改指 */ }
                if (state.Launcher.ReconcileMachineHome(out _, out var hit, inventory) is not null)
                    movedTo = hit;
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ProgressDone(0);
                    App.ReportPage(ex);
                    OfferAdoptIfNeeded(inventory);
                }));
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ProgressDone(0);
                if (movedTo is not null)
                {
                    state.Reload();
                    Toast($"本机 DSH 已跟随到 {movedTo}");
                }
                OfferAdoptIfNeeded(inventory);
            }));
        });
    }

    public void OfferAdoptIfNeeded(Inventory? inventory = null)
    {
        if (App.ShotPath is not null) return;
        if (_adoptBusy) return;
        var state = AppState.Current;
        var sniffed = new List<DshHome>(inventory?.Homes ?? []);
        try
        {
            var def = LaunchPolicy.DefaultDshHome(state.Settings.Launch.DshHome);
            if (DshSniff.LooksLikeHome(def)
                && sniffed.All(h => !LaunchPolicy.SamePath(h.Path, def)))
                sniffed.Add(new DshHome(def, [], 0, false, "DSH_HOME"));
        }
        catch { /* 默认 home 读不到就不补这一条 */ }

        List<string> have;
        try { have = state.Launcher.Instances().Select(i => i.Home).ToList(); }
        catch { have = []; }
        var pending = PendingAdopt.List(sniffed, have, state.Settings.Other.AdoptSkippedHomes);
        if (pending.Count == 0) return;

        _adoptBusy = true;
        try
        {
            var win = new Views.AdoptHomesWindow(pending) { Owner = this };
            if (win.ShowDialog() == true)
            {
                RememberSkipped(state, win.Skipped);
                _ = AdoptListed(win.Chosen);
                return;
            }
            if (win.RememberSkip) RememberSkipped(state, win.Skipped);
        }
        finally { _adoptBusy = false; }
    }

    void RememberSkipped(AppState state, IEnumerable<string> paths)
    {
        var list = state.Settings.Other.AdoptSkippedHomes;
        foreach (var path in paths)
            if (!list.Any(p => LaunchPolicy.SamePath(p, path))) list.Add(path);
        state.Settings.Other.Clamp();
        state.ApplySettings(state.Settings);
    }

    async Task AdoptListed(IReadOnlyList<string> homes)
    {
        var n = 0;
        foreach (var home in homes)
            if (await AdoptHomeAt(home, "", toast: false, name: PendingAdopt.SuggestName(home)))
                n++;
        if (n > 0) Toast(n == 1 ? "已收编 1 个" : $"已收编 {n} 个");
    }

    public async void AdoptNow()
    {
        var home = LaunchPolicy.DefaultDshHome(AppState.Current.Settings.Launch.DshHome);
        await AdoptHomeAt(home, "已收编为本机 DSH");
    }

    /// <summary>PCL「添加已有文件夹」：先选夹，再收编为实例。</summary>
    public void AdoptFolder()
    {
        var home = LaunchPolicy.DefaultDshHome(AppState.Current.Settings.Launch.DshHome);
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "添加已有文件夹" };
        if (System.IO.Directory.Exists(home)) dlg.InitialDirectory = home;
        if (dlg.ShowDialog(this) != true) return;
        if (string.IsNullOrWhiteSpace(dlg.FolderName)) return;
        _ = AdoptHomeAt(dlg.FolderName, "已添加这个文件夹");
    }

    async Task<bool> AdoptHomeAt(string home, string done, bool toast = true, string? name = null)
    {
        var state = AppState.Current;
        try
        {
            if (state.Releases.Count == 0)
                Toast("在装 DSH 发行号，随后收编");
            var version = await state.Launcher.EnsureRelease(null, CancellationToken.None);
            var inst = state.Launcher.AdoptHome(home, version, name);
            state.Reload();
            state.SelectedInstance = state.Instances.FirstOrDefault(i => i.Id == inst.Id);
            if (toast) Toast(done);
            return true;
        }
        catch (Exception ex)
        {
            Toast(PadError.Describe(ex), bad: true);
            return false;
        }
    }

    /// <summary>把探测到的 home 直接收编成实例（不弹选目录对话框，D36）。</summary>
    public Task AdoptHomePath(string home) => AdoptHomeAt(home, "已收编本机 DSH");

    void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    /// <summary>点关闭/Alt+F4：可缩托盘；真正退出走托盘菜单或 App.Quit。</summary>
    public void RequestClose()
    {
        if (App.ShotPath is null && !App.Exiting && App.CloseToTray)
        {
            HideToTray();
            return;
        }
        AppState.Current.SaveSelection();
        if (!App.Exiting && AppState.Current.Settings.Other.StopInstancesOnExit)
        {
            App.ExitGracefully(this);
            return;
        }
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (App.ShotPath is null && !App.Exiting && App.CloseToTray && IsVisible)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        // 关闭即退出（包括 Alt+F4 / 系统关闭）时，按设置先把实例停掉再真正退出。
        if (App.ShotPath is null && !App.Exiting && !App.CloseToTray
            && AppState.Current.Settings.Other.StopInstancesOnExit)
        {
            e.Cancel = true;
            App.ExitGracefully(this);
            return;
        }
        base.OnClosing(e);
    }

    public void HideToTray()
    {
        try { AppState.Current.SaveSelection(); } catch { /* ignore */ }
        var ui = AppState.Current.Settings.Ui;
        if (!ui.TrayHintDismissed && App.ShotPath is null && IsVisible)
        {
            // 第一次点 X：说明只是收进托盘，并给「跳设置 / 手滑了 / 好的 + 不再提醒」。
            var choice = AskTrayHint("⚠ 注意喵：",
                "退出仅会把窗口收进托盘，PAD 最小化进托盘！\n\n" +
                "想改成「关闭即退出 PAD」或者「退出时停掉所有实例」，去设置里改：",
                "点我跳转 → 设置 · 其他 · 关闭行为",
                "不再提醒", out var dismissed);
            if (dismissed)
            {
                ui.TrayHintDismissed = true;
                try { AppState.Current.SaveSettings(); } catch { /* ignore */ }
            }
            if (choice == HintChoice.Settings) { OpenSettingsCloseBehavior(); return; }
            if (choice == HintChoice.Cancel) return; // 手滑了：窗口留着
        }
        Hide();
    }

    /// <summary>只把托盘提示卡摆出来（不进入 DispatcherFrame），给 --shot 截图用。</summary>
    public void ShowTrayHintPreview()
    {
        MsgTitle.Text = "⚠ 注意喵：";
        MsgTitle.Foreground = (Brush)FindResource("AccentText");
        MsgLine.Fill = (Brush)FindResource("Accent");
        MsgDim.Background = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        MsgBody.Text = "退出仅会把窗口收进托盘，PAD 最小化进托盘！\n\n" +
                       "想改成「关闭即退出 PAD」或者「退出时停掉所有实例」，去设置里改：";
        MsgBody.Visibility = Visibility.Visible;
        MsgPreviewBox.Visibility = Visibility.Collapsed;
        MsgInput.Visibility = Visibility.Collapsed;
        MsgOk.Content = "好的";
        MsgOk.Style = (Style)FindResource("BtnPrimary");
        MsgOk.Visibility = Visibility.Visible;
        MsgCancel.Content = "我只是手滑了.....";
        MsgCancel.Visibility = Visibility.Visible;
        MsgCheck.Content = "不再提醒";
        MsgCheck.IsChecked = false;
        MsgCheck.Visibility = Visibility.Visible;
        MsgLinkButton.Inlines.Clear();
        MsgLinkButton.Inlines.Add(new Run("点我跳转 → 设置 · 其他 · 关闭行为"));
        MsgLink.Visibility = Visibility.Visible;
        MsgLayer.Visibility = Visibility.Visible;
        MsgCard.Opacity = 1;
    }

    /// <summary>托盘提示里「点我跳转」：打开 设置 · 其他 · 关闭行为。</summary>
    public void OpenSettingsCloseBehavior()
    {
        TabSettings.IsChecked = true;
        if (Page("settings") is SettingsView sv) sv.OpenCloseBehavior();
    }

    /// <summary>从托盘/第二个实例恢复窗口。</summary>
    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // 托盘点击/二次启动属于用户主动要求置前，短暂 Topmost 稳妥地把它带到前面。
        Topmost = true;
        Topmost = false;
    }

    public void SyncTrayState() => App.TrayIcon?.SyncCloseToTray(App.CloseToTray);

    HotKeyRegistration? _recentHotKey;
    Views.LocalModelsShopWindow? _shopWindow;

    /// <summary><c>--open recent|local-models</c>：自动化截图/QA 用，启动后打开对应浮动窗口。</summary>
    void OpenRequestedWindow()
    {
        switch ((App.OpenWindow ?? "").Trim().ToLowerInvariant())
        {
            case "recent":
                ShowRecent();
                break;
            case "local-models" or "models" or "local-models-shop" or "models-shop":
                ShowLocalModels();
                break;
        }
    }

    /// <summary>设置页改完快捷键后调一次，立刻按新组合重新注册。</summary>
    public void ReloadHotKey() => HookRecentHotKey();

    /// <summary>注册「最近会话」全局快捷键。被别的程序占用时静默记下，不影响其他功能。</summary>
    void HookRecentHotKey()
    {
        try { _recentHotKey?.Dispose(); } catch { /* 旧句柄已失效 */ }
        _recentHotKey = null;
        var spec = AppState.Current.Settings.Other.RecentHotkey;
        if (string.IsNullOrWhiteSpace(spec)) return;
        try
        {
            var reg = new HotKeyRegistration(this, spec);
            if (reg.Registered) reg.Pressed += ShowRecent;
            else if (reg.Error.Length > 0) Toast($"快捷键「{spec}」没注册上：{reg.Error}", bad: true);
            _recentHotKey = reg;
        }
        catch { /* 注册失败不影响主流程 */ }
    }

    /// <summary>托盘 / 快捷键：列出所有实例的最近会话，回车就在对应实例里打开。</summary>
    public void ShowRecent()
    {
        IReadOnlyList<Core.RecentSession> rows;
        try { rows = Core.RecentSessions.List(AppState.Current.Launcher); }
        catch (Exception ex) { Toast(PadError.Describe(ex), bad: true); return; }

        var win = new Views.RecentWindow(rows);
        win.Chosen += session => OpenRecent(session);
        win.Show();
        win.Activate();
        win.Focus();
    }

    void OpenRecent(Core.RecentSession session)
    {
        var inst = AppState.Current.Launcher.Instances()
            .FirstOrDefault(i => string.Equals(i.Id, session.InstanceId, StringComparison.OrdinalIgnoreCase));
        if (inst is null)
        {
            Toast("那个实例已经不在了", bad: true);
            return;
        }
        AppState.Current.SelectedInstance = inst;
        Toast($"正在打开「{session.Title}」…");
        _ = OpenRecentAsync(inst, session);
    }

    async Task OpenRecentAsync(Instance inst, Core.RecentSession session)
    {
        try
        {
            var plan = await Core.Jump.JumpPlanner.ExecuteAsync(
                AppState.Current.Launcher, inst, session.SessionId, null, execute: true);
            if (!plan.Executed)
                Toast(plan.Detail is { Length: > 0 } detail ? detail : "这个实例现在没有可用的界面，已停在 PAD 里", bad: true);
        }
        catch (Exception ex)
        {
            Toast(PadError.Describe(ex), bad: true);
        }
    }

    /// <summary>本地模型：探测 Ollama、列模型、拉一个。从托盘点进来时先把主窗口叫回来，否则对话框会跟着隐藏窗口一起看不见。</summary>
    public void ShowLocalModels()
    {
        if (!IsVisible) RestoreFromTray();
        if (_shopWindow is { IsVisible: true })
        {
            _shopWindow.Activate();
            return;
        }
        _shopWindow = new Views.LocalModelsShopWindow { Owner = this };
        _shopWindow.Show();
    }
}

/// <summary>A page that wants a chance to reload when it becomes visible.</summary>
public interface IRefreshable
{
    void OnShown();
}
