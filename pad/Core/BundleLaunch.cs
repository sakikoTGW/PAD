using System.Diagnostics;
using System.IO;

namespace Pad.Core;

/// <summary>
/// 窗口侧（GUI）的 bundle 入口：同一份 home / 实例，想用哪种形态看，就开哪种窗口。
///
/// Profile 决定「装什么」，Bundle 决定「怎么看见」（docs/PRODUCT.md）。启动实现全部
/// 复用 <see cref="Nodes"/>，和 <c>pad cli node open --bundle</c> 走同一条路 —— 账本、
/// 日志、停法因此完全一致，不会变成两套真相。
/// </summary>
public static class BundleLaunch
{
    /// <summary>TUI 没有握手，启动后活过几秒就算起来了；web 要等 announce 的 token URL。</summary>
    public static readonly TimeSpan TuiTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan WebTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(60);

    /// <summary>按指定形态启动一个 PAD 实例的某个 profile。</summary>
    public static async Task<Nodes.Opened> OpenAsync(
        Launcher launcher, Instance inst, string profile, Bundle bundle, PadSettings settings, CancellationToken ct)
    {
        var bin = launcher.DshBin(inst.Dsh.Version);
        if (!File.Exists(bin))
            throw new PadError("PA001", "the pinned release is not installed", $"instance `{inst.Id}`", bin,
                ["先装这个发行号"]);

        // LaunchFloat / Runner 的启动链会先把 PAD 配的凭据铺进这个 home；bundle 直连
        // Nodes 的路也必须做同一件事，否则同一个实例「从容器启动」和「从启动页启动」
        // 会看到两套环境。
        CredentialsGate.CopyDistributorToHome(launcher, inst, settings);

        // 同一个 home+profile 只留一个窗口：再开一个会挤掉账本里的那一行，旧进程就变成
        // 「还在跑、但 PAD 再也关不掉」。换形态请先关掉原来的。
        var live = Nodes.Ledger.Load(inst.Home)
            .FirstOrDefault(e => string.Equals(e.Profile, profile, StringComparison.OrdinalIgnoreCase) && Proc.Alive(e.Pid));
        if (live is not null && !string.Equals(live.Bundle, bundle.Kind, StringComparison.OrdinalIgnoreCase))
            throw new PadError("PA099", "that profile is already running in another bundle", inst.Home,
                $"profile {live.Profile} · bundle {live.Bundle} · pid {live.Pid}",
                ["先关掉它再换形态", "pad cli node list " + inst.Home]);

        var node = launcher.ResolveLaunchNode(inst, settings) ?? Proc.Which("node")
            ?? throw new PadError("PA011", "`node` was not found on PATH", "PATH", "node",
                ["装 Node.js 22.19+ 或 24+"]);

        var opened = bundle.Kind switch
        {
            "tui" => await Nodes.OpenTerminal(inst.Home, profile, bin, node,
                $"{inst.Name} · {profile}", TuiTimeout, ct, DeepseekEnv(launcher, inst.Home)),
            "web" => await Nodes.OpenWeb(inst.Home, profile, bin, node, null, WebTimeout, ct),
            _ => throw new PadError("PA085", "this bundle needs an executable; use the CLI", bundle.Id, inst.Home,
                ["pad cli node open <home> --bundle app|tauri --exe <exe>"]),
        };

        Record(inst.Home, opened);
        if (opened.Bundle == "web") Nodes.TryOpenWindow(opened.Url, out _);
        return opened;
    }

    /// <summary>
    /// 本机探测到的 TUI（不是 PAD 实例）：有它自己的启动脚本就照脚本跑 —— 那是作者写的
    /// 启动方式，PAD 不该替它猜；没有脚本才退回「home + profile 起终端」。
    /// </summary>
    public static async Task<Nodes.Opened> OpenTuiAsync(Launcher launcher, Tui tui, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(tui.Launcher) && File.Exists(tui.Launcher))
        {
            // PAD 的 API Key 不在这里就白配了：探测到的 TUI 是我们拉起来的进程，
            // 它和 launch-*.cmd 一样拿不到 PAD 的环境，所以套一层脚本把 key 传进去。
            var script = string.IsNullOrWhiteSpace(tui.Home)
                ? tui.Launcher
                : WriteTuiWrapper(tui, ReadDeepseekKey(launcher, tui.Home));
            var pid = StartConsole($"\"{script}\"", Path.GetDirectoryName(script)!);
            var opened = new Nodes.Opened(
                tui.Home ?? "", tui.Profile ?? "tui", "tui", 0, "", pid,
                string.IsNullOrWhiteSpace(tui.Home) ? "" : Path.Combine(tui.Home, ".pad", "logs", "tui.log"));
            Record(tui.Home, opened);
            return opened;
        }

        var inst = launcher.Instances()
            .FirstOrDefault(i => SamePath(i.Home, tui.Home ?? ""));
        if (inst is null)
            throw new PadError("PA160", "this TUI is not inside a PAD instance", tui.Package,
                tui.Home ?? tui.Launcher,
                ["先「添加已有文件夹」把它的 home 收编成实例", "或给它一个启动脚本"]);

        var profile = tui.Profile ?? inst.LastProfile
            ?? launcher.Profiles(inst).FirstOrDefault()?.Name ?? "tui";
        return await OpenAsync(launcher, inst, profile, Bundle.Tui, launcher.Settings(), ct);
    }

    /// <summary>本机探测到的桌面壳：拉它的 exe，DSH_HOME 指向目标 home（实例 home 优先）。</summary>
    public static Nodes.Opened OpenShell(Launcher launcher, Shell shell, Instance? target = null, bool restart = false)
    {
        if (string.IsNullOrWhiteSpace(shell.Exe) || !File.Exists(shell.Exe))
            throw new PadError("PA087", "no desktop shell executable at that path", shell.Exe ?? "(none)", null,
                ["重新探测一次", "或从开始菜单打开它"]);

        var home = string.IsNullOrWhiteSpace(target?.Home) ? shell.Home : target!.Home;
        var bundle = Path.GetFileNameWithoutExtension(shell.Exe)
            .Contains("tauri", StringComparison.OrdinalIgnoreCase) ? "tauri" : "app";

        // 同一个 exe 已经在跑：home 对得上就只聚焦；对不上要么拒绝，要么按调用方要求重启过去。
        var live = ShellRunning(shell.Exe);
        if (live.Count > 0)
        {
            var same = live.FirstOrDefault(x => string.IsNullOrWhiteSpace(home)
                ? string.IsNullOrWhiteSpace(x.Home)
                : LaunchPolicy.SamePath(x.Home ?? "", home));
            if (same.Pid > 0)
            {
                WindowFocus.Activate(same.Pid);
                return new Nodes.Opened(home ?? "", "desktop", bundle, 0, "", same.Pid, "", Verified: true);
            }
            if (!restart)
                throw new PadError("PA098", "the desktop shell is already running with a different home",
                    shell.Exe, string.Join(", ", live.Select(x => x.Home ?? "(none)")),
                    ["先退出它", "或让 PAD 重启到目标 home"]);
            foreach (var (pid, _) in live)
            {
                try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                catch { /* 已经退出 */ }
            }
            for (var i = 0; i < 40 && ShellRunning(shell.Exe).Count > 0; i++) System.Threading.Thread.Sleep(250);
        }

        var psi = new ProcessStartInfo(shell.Exe)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = string.IsNullOrWhiteSpace(Path.GetDirectoryName(shell.Exe)) ? Environment.CurrentDirectory : Path.GetDirectoryName(shell.Exe)!,
        };
        if (!string.IsNullOrWhiteSpace(home)) psi.Environment["DSH_HOME"] = home;
        if (ReadDeepseekKey(launcher, home) is { } key) psi.Environment["DEEPSEEK_API_KEY"] = key;

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            throw new PadError("PA025", "the desktop shell did not start", shell.Exe, ex.Message,
                ["检查这个 exe 是否还在", "它可能要求管理员权限或已在运行"]);
        }

        var opened = new Nodes.Opened(home ?? "", "desktop", bundle, 0, "", proc.Id, "");
        Record(home, opened);
        return opened;
    }

    /// <summary>同名 exe 的活进程（pid + 它环境里的 DSH_HOME）。</summary>
    internal static List<(int Pid, string? Home)> ShellRunning(string exe)
    {
        var list = new List<(int, string?)>();
        try
        {
            foreach (var p in ProcTable.Snapshot(900))
            {
                if (string.IsNullOrWhiteSpace(p.Exe) || !LaunchPolicy.SamePath(p.Exe, exe)) continue;
                if (!Proc.Alive(p.Pid)) continue;
                list.Add((p.Pid, p.DshHome));
            }
        }
        catch { /* 快照读不到就当没在跑 */ }
        return list;
    }

    /// <summary>关掉本机探测到的桌面端（按 exe 找进程，整树杀）。返回关掉的进程数。</summary>
    public static int CloseShell(Shell shell)
    {
        var killed = 0;
        foreach (var (pid, _) in ShellRunning(shell.Exe))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                killed++;
            }
            catch { /* 已经退出 */ }
        }
        return killed;
    }

    /// <summary>给子进程的环境里带上 PAD 配好的 DEEPSEEK_API_KEY（没配就返回 null）。</summary>
    static IReadOnlyDictionary<string, string>? DeepseekEnv(Launcher launcher, string? home) =>
        ReadDeepseekKey(launcher, home) is { } key
            ? new Dictionary<string, string> { ["DEEPSEEK_API_KEY"] = key }
            : null;

    /// <summary>
    /// 先认这个 home 自己的 <c>.credentials.yaml</c>，再退 PAD 的全局那份。
    /// 两边都没有 = 没配，调用方按「不带 key 启动」处理。
    /// </summary>
    static string? ReadDeepseekKey(Launcher launcher, string? home)
    {
        if (!string.IsNullOrWhiteSpace(home))
        {
            var local = Clean(CredentialsFile.ReadDeepseekKey(ReadText(Path.Combine(home, ".credentials.yaml"))));
            if (local.Length > 0) return local;
        }
        var global = Clean(CredentialsFile.ReadDeepseekKey(ReadText(launcher.GlobalCredentialsPath)));
        return global.Length > 0 ? global : null;
    }

    /// <summary>PAD 的 yaml 用 <c>"REF: key"</c> 标记直填值；交给子进程前把标记剥掉。</summary>
    static string Clean(string key)
    {
        var k = (key ?? "").Trim().Trim('"');
        if (k.StartsWith("REF:", StringComparison.OrdinalIgnoreCase)) k = k[4..].Trim();
        return k;
    }

    static string ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    /// <summary>
    /// 给探测到的 TUI 套一层启动脚本：先 <c>set DEEPSEEK_API_KEY</c>，再 call 它自己的脚本。
    /// 用文件而不是命令行传 key，进程列表里看不到它。
    /// </summary>
    static string WriteTuiWrapper(Tui tui, string? key)
    {
        var dir = Path.Combine(tui.Home!, ".pad");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "launch-tui.cmd");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("chcp 65001 >nul");
        if (key is not null)
            sb.AppendLine($"set \"DEEPSEEK_API_KEY={key.Replace("%", "%%")}\"");
        sb.AppendLine($"call \"{tui.Launcher}\" %*");
        File.WriteAllText(path, sb.ToString(), CmdEncoding());
        return path;
    }

    /// <summary>cmd.exe 按控制台 OEM 代码页读脚本；写成 OEM 才不会把中文路径读成乱码。</summary>
    static System.Text.Encoding CmdEncoding()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var oem = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            if (oem > 0) return System.Text.Encoding.GetEncoding(oem);
        }
        catch { /* provider 不可用就退 UTF-8 */ }
        return new System.Text.UTF8Encoding(false);
    }

    /// <summary>新开一个控制台跑命令（cmd /k：进程退出后窗口还在，报错看得见）。</summary>
    static int StartConsole(string command, string workingDirectory)
    {
        // Windows: cmd /d /k；macOS: 生成 .command 交给 Terminal.app。平台差异在 IPlatform 里。
        var proc = Platform.Current.StartConsole(command, workingDirectory);
        if (proc is null)
            throw new PadError("PA079", "could not start the terminal", command,
                "Platform.StartConsole returned null", ["检查这个启动脚本是否还在"]);
        return proc.Id;
    }

    static void Record(string? home, Nodes.Opened opened)
    {
        if (string.IsNullOrWhiteSpace(home)) return;
        Nodes.Ledger.Record(home, new Nodes.Running(
            opened.Profile, opened.Bundle, opened.Port, opened.Url, opened.Pid,
            DateTimeOffset.Now.ToString("o"), opened.LogPath, opened.Verified));
    }

    static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
