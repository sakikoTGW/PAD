using System.IO;
using System.Text.Json;

namespace Pad.Core;

/// <summary>
/// <c>pad cli sisyphus …</c> — experimental (<c>pad cli experimental enable sisyphus</c>).
/// status: read-only facts; setup: optional sandbox clone → checkpoint → install → verify.
/// </summary>
public static class SisyphusCli
{
    static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    const string UsageLine =
        "pad cli sisyphus status <instance> [--profile web] [--json] [--out f]\n" +
        "       pad cli sisyphus setup <instance> [--profile web] [--sisyphus <dir|tgz|npm>] [--preset <dir|tgz|npm>] [--sandbox <name>] [--json] [--out f]\n" +
        "       pad cli sisyphus sources";

    static readonly string[] Valued = ["--profile", "--sisyphus", "--preset", "--sandbox", "--out"];

    public static async Task<int> Run(Launcher launcher, string cmd, string[] rest, IProgress<string> log)
    {
        Cli.RequireExperiment(launcher, Sisyphus.Experiment, "sisyphus kit");
        var pos = Positionals(rest);
        var profile = Flag(rest, "--profile") ?? "web";
        switch (cmd)
        {
            case "sources":
                Emit(launcher, new { path = Sisyphus.SourcesPath(launcher), sources = Sisyphus.LoadSources(launcher) }, rest);
                return 0;

            case "status":
            {
                if (pos.Count < 1) return Usage();
                var inst = Need(launcher, pos[0]);
                var status = await Sisyphus.StatusAsync(launcher, inst, profile, CancellationToken.None);
                Report(launcher, status, rest);
                return status.Ready ? 0 : 1;
            }

            case "setup":
            {
                if (pos.Count < 1) return Usage();
                var inst = Need(launcher, pos[0]);
                var saved = Sisyphus.LoadSources(launcher);
                var sources = new SisyphusSources
                {
                    Sisyphus = Flag(rest, "--sisyphus") ?? saved.Sisyphus,
                    Preset = Flag(rest, "--preset") ?? saved.Preset,
                };
                if (Flag(rest, "--sandbox") is { } sandbox)
                {
                    log.Report($"克隆 {inst.Id} → {sandbox}（会话、数据一起复制，工作区共用）");
                    inst = launcher.CloneInstance(inst.Id, sandbox);
                }
                var status = await Sisyphus.SetupAsync(launcher, inst, profile, sources, log, CancellationToken.None);
                Report(launcher, status, rest);
                return 0;
            }

            default:
                return Usage();
        }
    }

    static void Report(Launcher launcher, SisyphusStatus status, string[] rest)
    {
        if (rest.Contains("--json") || Flag(rest, "--out") is not null)
        {
            Emit(launcher, status, rest);
            return;
        }
        Cli.Print($"{status.Instance} / {status.Profile} · DSH {status.Version} · {(status.Ready ? "就绪" : "未就绪")}");
        foreach (var p in status.Packages)
        {
            var rows = p.Rows.Count == 0 ? "无" : string.Join(" ", p.Rows.Select(r => r.Id + (r.Present && !r.Disabled ? "✓" : "✗")));
            Cli.Print($"  {p.Name} · 依赖 {(p.Dependency ? "✓" : "✗")} · bundles {(p.Bundle ? "✓" : "✗")} · 行 {rows}");
        }
        if (status.Presets.Count > 0) Cli.Print($"  预设 {string.Join(", ", status.Presets)}");
        Cli.Print($"  项目数据 {(status.Projects is { } n ? n + " 个项目" : "还没有")} · {status.DataPath}");
        if (status.Checkpoint is not null) Cli.Print($"  存档点 {status.Checkpoint}");
        foreach (var p in status.Problems) Cli.Print("  ! " + p);
        if (status.Ready) Cli.Print("  新开会话才会用到新预设；已有会话锁定原来的预设。");
    }

    static Instance Need(Launcher l, string id) =>
        l.Instance(id) ?? throw new PadError("PA026", "no such instance", $"instances/{id}", id,
            ["pad cli instance list"]);

    static void Emit(Launcher launcher, object value, string[] rest)
    {
        var json = JsonSerializer.Serialize(value, Pretty);
        var outPath = Flag(rest, "--out");
        if (string.IsNullOrWhiteSpace(outPath))
        {
            Cli.Payload(json);
            return;
        }
        var full = Path.GetFullPath(Path.IsPathFullyQualified(outPath) ? outPath : Path.Combine(launcher.Root, outPath));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, json, new System.Text.UTF8Encoding(false));
        Cli.Print($"wrote {full}");
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: " + UsageLine);
        return 2;
    }

    static string? Flag(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    static List<string> Positionals(string[] args)
    {
        var list = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (Array.IndexOf(Valued, args[i]) >= 0) { i++; continue; }
            if (args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            list.Add(args[i]);
        }
        return list;
    }
}
