using System.Windows;
using System.Windows.Controls;
using Pad.Core;
namespace Pad.Views;

public partial class LaunchView : UserControl, IRefreshable
{
    readonly MainWindow _win;
    readonly AppState _state = AppState.Current;

    public LaunchView(MainWindow win)
    {
        _ = _state.RestoreLastContainerAsync();   // 还原上次点的那一行
        InitializeComponent();
        _win = win;
        DataContext = _state;
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.CanLaunch) or nameof(AppState.NeedsSetup)
                or nameof(AppState.HasInstanceNoProfile) or nameof(AppState.StepTarget)
                or nameof(AppState.SelectedProfile) or nameof(AppState.SelectedInstance)
                or nameof(AppState.AnyRunning))
            {
                PaintRunBtn();
                PaintKey();
            }
        };
        PaintRunBtn();
    }

    public void OnShown()
    {
        if (VersionEntryGrid is not null)
            VersionEntryGrid.Visibility = _state.Settings.Ui.HideVersionEntry
                ? Visibility.Collapsed : Visibility.Visible;
        _state.RefreshRunning();
        PaintRunBtn();
        PaintKey();
        _ = _state.ReloadPacks();
    }

    void PaintKey()
    {
        if (KeyWarn is null) return;
        var probe = CredentialsGate.Inspect(_state.Launcher, _state.SelectedInstance, _state.Settings);
        Fx.SetShown(KeyWarn, !(probe.Configured));
    }


    /// <summary>启动成功后等目标窗口真的出现，再给“已启动”的反馈。</summary>
    async Task AwaitShellWindowAsync(DetectedShellRow shell, int pid)
    {
        for (var i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            if (WindowFocus.Activate(pid) || WindowFocus.ActivateByExe(shell.Source.Exe))
            {
                _win.Toast($"{shell.Name} 已打开（窗口已就绪）");
                PaintRunBtn();
                _state.RefreshRunning();
                return;
            }
        }
        // 启动器要为启动负责：窗口没出来就是失败，不能报“已启动”。
        // 启动器对结果负责：进程活着但没有窗口 = 启动失败，不能让人自己去任务栏/托盘找。
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch { /* 已经退出 */ }
        var err = new PadError("PA160", "the desktop app started but never showed a window", shell.Name,
            "pid " + pid + " · home " + shell.Source.Home,
            ["已经把它结束了；再启动一次试试", "还失败就看日志"]);
        _win.LaunchFailed(err.Render(), shell.Instance);
        _state.RefreshRunning();
    }


    /// <summary>桌面端此刻是否在跑：探测结果可能过期，这里按进程名实时确认一次。</summary>
    static bool ShellRunningNow(Shell shell)
    {
        if (shell.Pid is { } pid && Proc.Alive(pid)) return true;
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(shell.Exe);
            return name.Length > 0 && System.Diagnostics.Process.GetProcessesByName(name).Length > 0;
        }
        catch { return false; }
    }

    void PaintRunBtn()
    {
        if (RunLabel is null) return;
        if (RunCloseBtn is not null)
            Fx.SetShown(RunCloseBtn,
                _state.SelectedContainer is DetectedShellRow shellNow && ShellRunningNow(shellNow.Source)
                || _state.SelectedContainer is DetectedTuiRow { LocalRunning: true });

        // 探测到的桌面端已经在本机跑（同一个 home）：这个按钮能做的只有切窗口，
        // 不能"再启动一份"（Electron 单实例）。按钮文字必须说实话。
        if (_state.SelectedContainer is DetectedShellRow shellRow && ShellRunningNow(shellRow.Source))
        {
            var selHome = _state.SelectedInstance?.Home;
            if (string.IsNullOrWhiteSpace(selHome) || LaunchPolicy.SamePath(shellRow.Source.Home ?? "", selHome))
            {
                RunLabel.Text = "切到窗口";
                if (RunIcon is not null) RunIcon.Glyph = (System.Windows.Media.Geometry)FindResource("I.Play");
                return;
            }
        }
        if (_state.SelectedContainer is DetectedTuiRow tuiRow && tuiRow.LocalRunning)
        {
            RunLabel.Text = "切到终端";
            return;
        }

        // 和「启动」是同一个按钮：这个 profile 在跑，它就变成「关闭」。
        var running = RunningProfiles();
        if (running.Count > 0)
        {
            RunLabel.Text = "关闭";
            if (RunIcon is not null) RunIcon.Glyph = (System.Windows.Media.Geometry)FindResource("I.Stop");
            return;
        }

        if (RunIcon is not null) RunIcon.Glyph = (System.Windows.Media.Geometry)FindResource("I.Play");
        if (_state.CanLaunch) RunLabel.Text = "启动";
        else if (_state.StepTarget == "download") RunLabel.Text = "下载 DSH";
        else RunLabel.Text = "版本选择";
    }

    /// <summary>当前选中的 profile/容器 在 PAD 账本里还活着的节点。</summary>
    List<string> RunningProfiles()
    {
        var inst = _state.SelectedInstance;
        if (inst is null) return [];
        if (_state.SelectedContainer is DetectedTuiRow or DetectedShellRow) return [];
        var name = (_state.SelectedContainer as BundleRow)?.Profile.Name ?? _state.SelectedProfile?.Name;
        if (name is null) return [];

        // 选中的是某一种 bundle 时，只认账本里同 bundle 的那条；
        // 同一 profile 的另一形态在跑，不能让它把按钮变成「关闭」（那会关掉另一个窗口）。
        var bundle = (_state.SelectedContainer as BundleRow)?.BundleKind;
        var live = Nodes.Ledger.Load(inst.Home)
            .Where(e => string.Equals(e.Profile, name, StringComparison.OrdinalIgnoreCase) && Proc.Alive(e.Pid))
            .ToList();
        if (live.Count == 0)
            return _state.Running.Any(r => r.Entry.Instance == inst.Id && r.Profile == name) ? [name] : [];
        if (bundle is null) return live.Select(e => e.Profile).ToList();
        return live.Where(e => string.Equals(e.Bundle, bundle, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Profile)
            .ToList();
    }

    /// <summary>这个 profile 现在以哪种 bundle 在跑（没有就 null）。</summary>
    string? RunningBundle(Instance inst, string profile)
    {
        var live = Nodes.Ledger.Load(inst.Home)
            .Where(e => string.Equals(e.Profile, profile, StringComparison.OrdinalIgnoreCase) && Proc.Alive(e.Pid))
            .ToList();
        if (live.Count > 0) return live[0].Bundle;
        return _state.Running.Any(r => r.Entry.Instance == inst.Id && r.Profile == profile) ? "unknown" : null;
    }

    public void RevealVersionEntry()
    {
        if (VersionEntryGrid is not null) VersionEntryGrid.Visibility = Visibility.Visible;
    }

    void Step_Click(object sender, RoutedEventArgs e) => GoToStep();

    void Adopt_Click(object sender, RoutedEventArgs e) => _win.AdoptFolder();

    void GoToStep()
    {
        if (_state.StepTarget == "download") _win.TabDownload.IsChecked = true;
        else _win.ShowInner("versions", "版本选择");
    }

    async void Run_Click(object sender, RoutedEventArgs e)
    {
            _state.SaveSelection();   // 记住这次点的是哪一行（重启后还原）
        try
        {
            // 同一个按钮兼作关闭：这个 profile 在跑就先关，不再起第二个。
            if (RunningProfiles() is { Count: > 0 } runningProfiles && _state.SelectedInstance is { } runningInst)
            {
                var closed = Nodes.Close(runningInst.Home, runningProfiles[0]);
                _state.Runner.Stop(runningInst.Id, runningProfiles[0]);
                _state.RefreshRunning();
                PaintRunBtn();
                _win.Toast(closed > 0 ? $"已关闭 {runningProfiles[0]}" : "进程已经退出，账本已清理");
                return;
            }

            // 容器列表里点的是探测到的 TUI / 桌面端：按它自己的入口启动（PAD 只是拉起它）。
            switch (_state.SelectedContainer)
            {
                case DetectedTuiRow tui when !tui.LocalRunning:
                {
                    var opened = await BundleLaunch.OpenTuiAsync(_state.Launcher, tui.Source, CancellationToken.None);
                    _win.Toast($"已启动 {tui.Name}（终端窗口，pid {opened.Pid}）");
                    _state.RefreshRunning();
                    return;
                }
                case DetectedTuiRow tui:
                    _win.Toast($"{tui.Name} 已经在跑（不是 PAD 启动的）；从任务栏切到它的终端窗口。");
                    return;
                case DetectedShellRow shell:
                {
                    var target = _state.SelectedInstance;
                    var realInstance = target is not null
                        && !target.Id.StartsWith("detected-", StringComparison.OrdinalIgnoreCase);
                    var wantHome = !string.IsNullOrWhiteSpace(target?.Home) ? target!.Home : shell.Source.Home;

                    // 已经在跑：home 一致（或不知道归属）→ 只切窗口；
                    // 选中的是真实实例、但 home 不一致 → 问一次，重启过去（单实例只能这样换）。
                    if (shell.LocalRunning)
                    {
                        var sameHome = !string.IsNullOrWhiteSpace(wantHome)
                            && LaunchPolicy.SamePath(shell.Source.Home ?? "", wantHome);
                        if (!realInstance || string.IsNullOrWhiteSpace(wantHome) || sameHome)
                        {
                            var focused = WindowFocus.Activate(shell.Source.Pid ?? 0)
                                || WindowFocus.ActivateByExe(shell.Source.Exe);
                            _win.Toast(focused
                                ? $"已切到 {shell.Name}（pid {shell.Source.Pid}）"
                                : $"没找到 {shell.Name} 的窗口（pid {shell.Source.Pid}）");
                            return;
                        }
                        var go = _win.Ask("桌面端用的是另一个 home",
                            $"现在：{shell.Source.Home}\n要重启到：{wantHome}\n\n重启会关闭当前桌面端窗口。", warn: true);
                        if (!go) return;
                        var restarted = BundleLaunch.OpenShell(_state.Launcher, shell.Source, target, restart: true);
                        _win.Toast($"已重启到 home {restarted.Home}（pid {restarted.Pid}）");
                        _state.RefreshRunning();
                        return;
                    }

                    _win.Toast($"正在启动 {shell.Name}…");
                    var opened = BundleLaunch.OpenShell(_state.Launcher, shell.Source, target);
                    _state.RefreshRunning();
                    // 进程起来不等于窗口出来：等窗口真的出现再报“已启动”。
                    _ = AwaitShellWindowAsync(shell, opened.Pid);
                    return;
                }

            }

            // profile 拆出来的 bundle 行：这次要用哪种「怎么看见」是行自己带的。
            if (_state.SelectedContainer is BundleRow bundleRow)
            {
                // 同一 profile 的另一种形态正在跑：说明清楚，别静默换成「关闭」把那个窗口关掉。
                var runningBundle = RunningBundle(bundleRow.Instance, bundleRow.Profile.Name);
                if (runningBundle is not null
                    && !string.Equals(runningBundle, bundleRow.BundleKind, StringComparison.OrdinalIgnoreCase))
                {
                    _win.Toast($"profile {bundleRow.Profile.Name} 正以 {runningBundle} 在跑；先关掉再换形态");
                    return;
                }

                var bundle = Bundle.For(bundleRow.BundleKind);
                if (bundle is null) return;
                var opened = await BundleLaunch.OpenAsync(_state.Launcher, bundleRow.Instance,
                    bundleRow.Profile.Name, bundle, _state.Settings, CancellationToken.None);
                _win.Toast($"已启动 {bundleRow.InstanceName} · {bundleRow.Profile.Name}（{bundle.Shows}，pid {opened.Pid}）");
                _state.RefreshRunning();
                await Task.Delay(700);   // 进程与账本落盘需要一点时间，晚一拍再刷新按钮
                _state.RefreshRunning();
                PaintRunBtn();
                return;
            }

            var profile = _state.SelectedProfile ?? _state.SelectedContainer;

            if (profile is null)
            {
                GoToStep();
                return;
            }

            // `desktop` profile 由桌面端（Electron/Tauri）独占管理，CLI 跑它会直接退出：
            //   error: profile "desktop" is managed exclusively by the Electron application
            // 所以这个 profile 的"启动"就等于"拉起/切到该 home 的官方桌面端"。
            if (string.Equals(profile.Name, "desktop", StringComparison.OrdinalIgnoreCase))
            {
                var home = profile.Instance.Home;
                Shell? shell = null;
                try
                {
                    var inv = await Task.Run(() => Discover.Run(_state.Launcher, [], CancellationToken.None));
                    shell = inv.Shells.FirstOrDefault(s =>
                        !string.IsNullOrWhiteSpace(s.Home) && LaunchPolicy.SamePath(s.Home, home));
                }
                catch { /* 探测失败就按下面提示 */ }
                if (shell is null)
                {
                    _win.Toast("desktop profile 由桌面端管理，但没找到这个 home 的桌面端；请直接打开 DeepSeek Harness");
                    return;
                }
                var opened = BundleLaunch.OpenShell(_state.Launcher, shell, profile.Instance);
                _win.Toast(opened.Verified
                    ? $"桌面端已在运行（pid {opened.Pid}），已切到窗口"
                    : $"已启动桌面端（pid {opened.Pid}，home {opened.Home}）");
                _state.RefreshRunning();
                return;
            }

            var key = CredentialsGate.Inspect(_state.Launcher, profile.Instance, _state.Settings);
            if (!key.Configured)
            {
                _win.OpenSettingsApi();
                _win.LaunchFailed(CredentialsFile.MissingError(key.Location).Render(), profile.Instance);
                return;
            }
            // 一个 profile 同时提供两种 bundle 是合法的（profile 决定装什么）；
            // 只是启动时要挑一个。这里先说明这次按哪个开，选择器是下一步。
            if (profile.Profile.UiBundles.Count > 1)
                _win.Toast($"{profile.Name} 提供 " + string.Join(" / ", profile.Profile.UiBundles) +
                           $" 两种 bundle，这次按 {(profile.Profile.IsWeb ? "web" : "tui")} 启动");
            _win.ShowTab("manage");
            LaunchFloat.Open(_win, profile);
        }
        catch (Exception ex)
        {
            _win.Toast(PadError.Describe(ex), bad: true);
        }
    }


    /// <summary>关闭本机探测到的桌面端 / TUI（PAD 没启动它，但可以关它）。</summary>
    async void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var closed = 0;
            switch (_state.SelectedContainer)
            {
                case DetectedShellRow shell:
                    closed = BundleLaunch.CloseShell(shell.Source);
                    break;
                case DetectedTuiRow tui when tui.Source.Pid is { } pid && Proc.Alive(pid):
                    try
                    {
                        using var p = System.Diagnostics.Process.GetProcessById(pid);
                        p.Kill(entireProcessTree: true);
                        closed++;
                    }
                    catch { /* 已经退出 */ }
                    break;
            }
            _win.Toast(closed > 0 ? $"已关闭（{closed} 个进程）" : "进程已经退出");
            _state.RefreshRunning();
            PaintRunBtn();
        }
        catch (Exception ex)
        {
            _win.Toast(PadError.Describe(ex), bad: true);
        }
        await Task.CompletedTask;
    }
    void Manage_Click(object sender, RoutedEventArgs e) => _win.ShowTab("manage");

    void Versions_Click(object sender, RoutedEventArgs e) => _win.ShowInner("versions", "版本选择");

    void Instance_Click(object sender, RoutedEventArgs e)
    {
        if (_state.SelectedInstance is null) return;
        _win.ShowInner("instance", "版本设置");
    }
}
