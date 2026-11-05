using System.IO;
using System.Diagnostics;

namespace Pad.Core;

/// <summary>
/// 「我在哪」：从 PAD 自己往上走父进程链，找出是谁启动的这次命令
/// （哪个 DSH home / profile / bundle / 实例 / cwd）。
/// 给 DSH 里的 agent 用：它自己看不到自己的启动参数，PAD 能读 PEB。
/// </summary>
public static class SelfWhere
{
    public sealed record Hop(int Pid, int ParentPid, string? Exe, string? Home, string? Profile, string? CommandLine);

    public static object Run(Launcher launcher)
    {
        var chain = new List<Hop>();
        ProcInfo? harness = null;
        ProcInfo? shell = null;
        var pid = Environment.ProcessId;

        for (var depth = 0; depth < 16 && pid > 0; depth++)
        {
            ProcInfo? info;
            try { info = ProcTable.Read(pid); }
            catch { info = null; }
            if (info is null) break;

            chain.Add(new Hop(info.Pid, info.ParentPid,
                info.Exe,
                string.IsNullOrWhiteSpace(info.DshHome) ? null : info.DshHome,
                info.DshProfile ?? info.ProfileArg,
                info.CommandLine));

            if (harness is null && !string.IsNullOrWhiteSpace(info.DshHome)) harness = info;
            if (shell is null && IsShellExe(info.Exe)) shell = info;
            pid = info.ParentPid;
        }

        // 没有 DSH_HOME 的进程（例如 node 自己）：命令行里带 bin.js 的那个就是 harness。
        harness ??= chain
            .Select(h => h.CommandLine ?? "")
            .Select((cmd, i) => (cmd, i))
            .Where(x => x.cmd.Contains("bin.js", StringComparison.OrdinalIgnoreCase)
                        || x.cmd.Contains("@deepseek-ai/dsh", StringComparison.OrdinalIgnoreCase))
            .Select(x => new ProcInfo(chain[x.i].Pid, chain[x.i].ParentPid, chain[x.i].Exe ?? "(node)",
                chain[x.i].CommandLine, null, chain[x.i].Profile, null, null))
            .FirstOrDefault();

        var home = harness?.DshHome ?? Environment.GetEnvironmentVariable("DSH_HOME");
        var profile = harness?.DshProfile
                      ?? harness?.ProfileArg
                      ?? Environment.GetEnvironmentVariable("DSH_PROFILE");

        Instance? instance = null;
        if (!string.IsNullOrWhiteSpace(home))
        {
            try
            {
                instance = launcher.Instances()
                    .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Home) && LaunchPolicy.SamePath(i.Home, home!));
            }
            catch { /* 实例列表坏了不影响自报 */ }
        }

        var shellExe = shell?.Exe;
        return new
        {
            cwd = SafeCwd(),
            pid = Environment.ProcessId,
            home,
            profile,
            instance = instance?.Id,
            instanceName = instance?.Name,
            bundle = BundleKindOf(shellExe),
            shellExe,
            shellPid = shell?.Pid,
            chain,
        };
    }

    static string SafeCwd()
    {
        try { return Directory.GetCurrentDirectory(); } catch { return ""; }
    }

    static bool IsShellExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return false;
        var name = Path.GetFileNameWithoutExtension(exe);
        return name.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase)
            || name.Contains("deepseek-harness-desktop", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DSH Tauri", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DSH Desktop", StringComparison.OrdinalIgnoreCase);
    }

    static string? BundleKindOf(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var name = Path.GetFileNameWithoutExtension(exe);
        if (name.Contains("tauri", StringComparison.OrdinalIgnoreCase)
            || name.Contains("harness-desktop", StringComparison.OrdinalIgnoreCase)) return "tauri";
        if (name.Contains("DeepSeek Harness", StringComparison.OrdinalIgnoreCase)) return "official-electron";
        return "third-party-electron";
    }
}
