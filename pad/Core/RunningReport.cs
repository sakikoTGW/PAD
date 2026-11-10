using System.IO;

namespace Pad.Core;

/// <summary>
/// 「谁在跑 + 它跑的是什么配置」：PAD 自己启动的 run（runtime.json / nodes.json）
/// 加上本机探测到的桌面端 / TUI。比 <c>ps</c> 多给出实例配置（home/版本/workspace/bundles）。
/// </summary>
public static class RunningReport
{
    public static object Run(Launcher launcher)
    {
        var instances = launcher.Instances().ToList();
        Instance? Match(string? home) => string.IsNullOrWhiteSpace(home)
            ? null
            : instances.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Home) && LaunchPolicy.SamePath(i.Home, home!));

        static IReadOnlyList<string> BundlesOf(Launcher l, Instance? inst, string? profile)
        {
            if (inst is null || string.IsNullOrWhiteSpace(profile)) return [];
            try
            {
                var p = l.Profiles(inst).FirstOrDefault(x =>
                    string.Equals(x.Name, profile, StringComparison.OrdinalIgnoreCase));
                return p?.Bundles ?? [];
            }
            catch { return []; }
        }

        var padRuns = new List<object>();
        foreach (var e in launcher.Runtime().Entries)
        {
            if (e.Pid > 0 && !Proc.Alive(e.Pid)) continue;
            var inst = instances.FirstOrDefault(i => i.Id == e.Instance);
            string? bundle = null;
            try
            {
                bundle = Nodes.Ledger.Load(inst?.Home ?? "")
                    .FirstOrDefault(x => string.Equals(x.Profile, e.Profile, StringComparison.OrdinalIgnoreCase))?.Bundle;
            }
            catch { /* 账本读不到不阻断 */ }

            padRuns.Add(new
            {
                source = "pad",
                instance = e.Instance,
                name = inst?.Name,
                home = inst?.Home,
                version = inst?.Dsh.Version ?? e.Version,
                profile = e.Profile,
                bundle,
                pid = e.Pid,
                port = e.Port,
                startedAt = e.StartedAt,
                workspace = inst?.Workspace?.Path,
                bundles = BundlesOf(launcher, inst, e.Profile),
            });
        }

        Inventory inv;
        try { inv = Discover.Run(launcher, [], CancellationToken.None); }
        catch { inv = Inventory.Empty; }

        var detected = new List<object>();
        foreach (var s in inv.Shells.Where(x => x.Running))
        {
            var inst = Match(s.Home);
            detected.Add(new
            {
                source = "detected",
                kind = BundleKindOf(s.Exe),
                instance = inst?.Id,
                name = inst?.Name,
                home = s.Home,
                version = s.Version,
                pid = s.Pid,
                exe = s.Exe,
            });
        }
        foreach (var t in inv.Tuis.Where(x => x.Running))
        {
            var inst = Match(t.Home);
            detected.Add(new
            {
                source = "detected",
                kind = "tui",
                instance = inst?.Id,
                name = inst?.Name,
                home = t.Home,
                profile = t.Profile,
                pid = t.Pid,
                package = t.Package,
            });
        }

        var rows = Schedule.List(launcher, inv).Select(r => new
        {
            r.Id,
            r.Kind,
            r.Name,
            r.Home,
            r.Running,
            r.Pid,
            gate = Schedule.Gate(r),
        });

        return new { padRuns, detected, rows };
    }

    static string BundleKindOf(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return "unknown";
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Contains("tauri", StringComparison.OrdinalIgnoreCase)
            || name.Contains("harness-desktop", StringComparison.OrdinalIgnoreCase)) return "tauri";
        if (name.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase)) return "official-electron";
        return "third-party-electron";
    }
}
