using System.Diagnostics;
using System.IO;

namespace Pad.Core;

/// <summary>
/// 本机每一份 DSH 的名单，以及开/关的唯一入口。窗口和 <c>pad cli</c> 都调这里，
/// 禁止各自再写一套分支（见 plugin/skills/pad：同一套 Core，两边不能有两种说法）。
/// </summary>
public sealed record ScheduleRow(
    string Id,
    string Kind,
    string Name,
    string? Home,
    bool Running,
    int? Pid = null,
    string? Profile = null,
    Instance? Instance = null,
    Shell? Shell = null,
    Tui? Tui = null);

public static class Schedule
{
    /// <summary>
    /// PAD 实例 + 探测到的 home / 桌面端 / TUI。同一条 home 只留实例那一行。
    /// 开/关按 <see cref="Gate"/>：pad / shell / tui / adopt。
    /// </summary>
    public static IReadOnlyList<ScheduleRow> List(Launcher launcher, Inventory inventory)
    {
        var instances = launcher.Instances().ToList();
        Instance? Match(string? home) => string.IsNullOrWhiteSpace(home)
            ? null
            : instances.FirstOrDefault(i => LaunchPolicy.SamePath(i.Home, home));

        var live = launcher.Runtime().Entries;
        var rows = new List<ScheduleRow>();
        foreach (var inst in instances)
        {
            var homeRun = inventory.Homes.Any(h => LaunchPolicy.SamePath(h.Path, inst.Home) && h.Running);
            var shell = inventory.Shells.FirstOrDefault(s => LaunchPolicy.SamePath(s.Home, inst.Home) && s.Running);
            var tui = inventory.Tuis.FirstOrDefault(t => LaunchPolicy.SamePath(t.Home, inst.Home) && t.Running);
            var tracked = live.Any(e =>
                string.Equals(e.Instance, inst.Id, StringComparison.OrdinalIgnoreCase)
                && (e.Pid <= 0 || Proc.Alive(e.Pid)));
            var running = homeRun || shell is not null || tui is not null || tracked;
            rows.Add(FromInstance(inst, inst.LastProfile, running, shell?.Pid ?? tui?.Pid));
        }

        foreach (var home in inventory.Homes)
        {
            if (instances.Any(i => LaunchPolicy.SamePath(i.Home, home.Path))) continue;
            var name = Path.GetFileName(home.Path.TrimEnd('\\', '/'));
            rows.Add(new ScheduleRow(
                "home:" + home.Path,
                "home",
                string.IsNullOrWhiteSpace(name) ? home.Path : name,
                home.Path,
                home.Running));
        }

        foreach (var shell in inventory.Shells)
            rows.Add(FromShell(shell, Match(shell.Home)));

        foreach (var tui in inventory.Tuis)
            rows.Add(FromTui(tui, Match(tui.Home)));

        return rows;
    }

    /// <summary>当场探测再合并。探测失败就只剩 PAD 实例。</summary>
    public static IReadOnlyList<ScheduleRow> List(Launcher launcher)
    {
        Inventory inv;
        try { inv = Discover.Run(launcher, [], CancellationToken.None); }
        catch { inv = Inventory.Empty; }
        return List(launcher, inv);
    }

    public static ScheduleRow FromInstance(Instance inst, string? profile = null, bool running = false, int? pid = null) =>
        new(inst.Id, "instance", inst.Name, inst.Home, running, pid, profile ?? inst.LastProfile, inst);

    public static ScheduleRow FromShell(Shell shell, Instance? inst = null) =>
        new("shell:" + shell.Exe,
            "shell",
            Path.GetFileNameWithoutExtension(shell.Exe),
            shell.Home,
            shell.Running,
            shell.Pid,
            "desktop",
            inst,
            shell);

    public static ScheduleRow FromTui(Tui tui, Instance? inst = null) =>
        new("tui:" + (tui.Package ?? "") + ":" + (tui.Profile ?? ""),
            "tui",
            tui.Package ?? "",
            tui.Home,
            tui.Running,
            tui.Pid,
            tui.Profile,
            inst,
            Tui: tui);

    public static string Gate(ScheduleRow row) => row.Kind switch
    {
        "instance" => "pad",
        "shell" => "shell",
        "tui" => "tui",
        "home" => "adopt",
        _ => row.Kind,
    };

    public static ScheduleRow? Find(IEnumerable<ScheduleRow> rows, string id) =>
        rows.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>同步入口：没收编的 home 直接 PA161；桌面端走 <see cref="BundleLaunch.OpenShell"/>。</summary>
    public static Nodes.Opened Open(Launcher launcher, ScheduleRow row, Instance? target = null, bool restart = false)
    {
        switch (Gate(row))
        {
            case "adopt":
                throw new PadError("PA161", "this home is not a PAD instance", row.Home ?? row.Id, null,
                    ["pad cli adopt " + (row.Home ?? row.Id)]);
            case "shell" when row.Shell is { } shell:
                return BundleLaunch.OpenShell(launcher, shell, target ?? row.Instance, restart);
            default:
                throw new PadError("PA085", "this row needs OpenAsync", row.Id, row.Kind,
                    ["pad cli run " + row.Id]);
        }
    }

    public static async Task<Nodes.Opened> OpenAsync(
        Launcher launcher,
        ScheduleRow row,
        PadSettings settings,
        CancellationToken ct,
        string? profile = null,
        Instance? target = null,
        bool restart = false,
        Bundle? bundle = null)
    {
        switch (Gate(row))
        {
            case "adopt":
                return Open(launcher, row);
            case "shell":
                return Open(launcher, row, target, restart);
            case "tui" when row.Tui is { } tui:
                return await BundleLaunch.OpenTuiAsync(launcher, tui, ct);
            case "pad" when row.Instance is { } inst:
            {
                var prof = profile ?? row.Profile
                    ?? inst.LastProfile
                    ?? launcher.Profiles(inst).FirstOrDefault()?.Name
                    ?? throw new PadError("PA240", "instance has no profile", $"instances/{inst.Id}", null,
                        ["pad cli profile add " + inst.Id + " web web"]);
                if (string.Equals(prof, "desktop", StringComparison.OrdinalIgnoreCase))
                {
                    var inv = Discover.Run(launcher, [], ct);
                    var shell = inv.Shells.FirstOrDefault(s =>
                        !string.IsNullOrWhiteSpace(s.Home) && LaunchPolicy.SamePath(s.Home, inst.Home))
                        ?? throw new PadError("PA087", "no desktop shell for that home", inst.Home, null,
                            ["直接打开 DeepSeek Harness"]);
                    return BundleLaunch.OpenShell(launcher, shell, inst, restart);
                }
                if (bundle is not null)
                    return await BundleLaunch.OpenAsync(launcher, inst, prof, bundle, settings, ct);
                var launched = new Runner(launcher).Launch(inst, prof, settings, detachWebOutput: true);
                return new Nodes.Opened(inst.Home, prof, bundle?.Kind ?? "", launched.WebUrl is { } u && Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Port : 0,
                    launched.WebUrl ?? "", launched.Pid, launched.LogPath ?? "");
            }
            default:
                throw new PadError("PA026", "no such instance", row.Id, row.Kind, ["pad cli status"]);
        }
    }

    public static int Close(Launcher launcher, ScheduleRow row, string? profile = null)
    {
        switch (row.Kind)
        {
            case "shell" when row.Shell is { } shell:
                return BundleLaunch.CloseShell(shell);
            case "tui":
            {
                var pid = row.Pid ?? row.Tui?.Pid;
                if (pid is not { } live || !Proc.Alive(live)) return 0;
                try
                {
                    using var p = Process.GetProcessById(live);
                    p.Kill(entireProcessTree: true);
                    return 1;
                }
                catch { return 0; }
            }
            case "instance" when row.Instance is { } inst:
            {
                var prof = profile ?? row.Profile;
                var n = Nodes.Close(inst.Home, prof);
                if (!string.IsNullOrWhiteSpace(prof))
                    new Runner(launcher).Stop(inst.Id, prof);
                return n;
            }
            default:
                return 0;
        }
    }
}
