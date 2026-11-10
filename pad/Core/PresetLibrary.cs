using System.IO;

namespace Pad.Core;

/// <summary>
/// 跨实例的 preset 库：PAD 自己的 <c>&lt;launcher&gt;/library/presets/&lt;id&gt;/</c>。
/// 库里的 preset 不属于任何实例；绑定时才复制进目标 home 的 <c>.agent-presets/</c>，
/// 所以一个库可以同时喂多个实例，各实例仍保有自己的副本。
/// </summary>
public static class PresetLibrary
{
    public static string Root(Launcher launcher) =>
        Path.Combine(launcher.Root, "library", "presets");

    static bool LooksLikePreset(string dir) =>
        File.Exists(Path.Combine(dir, "agent.cordis.yml")) || File.Exists(Path.Combine(dir, "preset.yml"));

    /// <summary>库里的 preset id 列表（目录名即 id）。</summary>
    public static IReadOnlyList<string> List(Launcher launcher)
    {
        var root = Root(launcher);
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Where(LooksLikePreset)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Dir(Launcher launcher, string id) => Path.Combine(Root(launcher), id);

    /// <summary>把一份 preset 目录收进库（同 id 覆盖前先备份）。</summary>
    public static string Add(Launcher launcher, string sourceDir, string? id = null)
    {
        if (!Directory.Exists(sourceDir))
            throw new PadError("PA170", "no such preset directory", sourceDir, sourceDir, ["给一个含 agent.cordis.yml 的目录"]);
        if (!LooksLikePreset(sourceDir))
            throw new PadError("PA171", "that folder is not an agent preset", sourceDir, sourceDir,
                ["需要 agent.cordis.yml 或 preset.yml"]);
        var name = string.IsNullOrWhiteSpace(id) ? Path.GetFileName(sourceDir.TrimEnd('\\', '/')) : id!;
        if (string.IsNullOrWhiteSpace(name))
            throw new PadError("PA172", "could not derive a preset id", sourceDir, sourceDir, ["显式指定 id"]);
        var dest = Dir(launcher, name);
        Directory.CreateDirectory(Root(launcher));
        if (Directory.Exists(dest))
        {
            var backup = dest + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Directory.Move(dest, backup);
        }
        CopyDir(sourceDir, dest);
        return dest;
    }

    public static void Remove(Launcher launcher, string id)
    {
        var dir = Dir(launcher, id);
        if (!Directory.Exists(dir))
            throw new PadError("PA173", "no such preset in library", id, dir, ["pad cli preset lib list"]);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>把库里的 preset 装进某个实例的 home（已存在则先备份）。返回目标目录。</summary>
    public static string Install(Launcher launcher, Instance inst, string id)
    {
        var src = Dir(launcher, id);
        if (!Directory.Exists(src))
            throw new PadError("PA173", "no such preset in library", id, src, ["pad cli preset lib list"]);
        if (UsesPresetRows(launcher, inst.Dsh.Version))
            throw new PadError("PA175", "this release no longer reads .agent-presets", $"instance `{inst.Id}`",
                $"DSH {inst.Dsh.Version} defines agent presets as @deepseek-ai/dsh-agent-preset rows inside a bundle",
                ["把预设写成组合包：package.json 带 \"dsh\": { \"bundle\": { \"patch\": \"./cordis.patch.yml\" } }，patch 里 insert 一行 preset-<id>",
                 "再用 pad cli plugin add <instance> <profile> <目录> 装进 profile"]);
        var dest = Path.Combine(inst.Home, ".agent-presets", id);
        Directory.CreateDirectory(Path.Combine(inst.Home, ".agent-presets"));
        if (Directory.Exists(dest))
        {
            var backup = dest + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            Directory.Move(dest, backup);
        }
        CopyDir(src, dest);
        return dest;
    }

    /// <summary>The release ships the preset-row plugin, so presets come from bundles and the
    /// old per-home directory is dead weight.</summary>
    public static bool UsesPresetRows(Launcher launcher, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var nm = Path.Combine(launcher.VersionDir(version), "node_modules");
        return Directory.Exists(Path.Combine(nm, ".pnpm", "node_modules", "@deepseek-ai", "dsh-agent-preset"))
               || Directory.Exists(Path.Combine(nm, "@deepseek-ai", "dsh-agent-preset"));
    }

    /// <summary>实例里有没有这个 preset（用户层）。</summary>
    public static bool Installed(Instance inst, string id) =>
        Directory.Exists(Path.Combine(inst.Home, ".agent-presets", id));

    /// <summary>把实例里的 preset 收进库（跨实例复制）。</summary>
    public static string AddFromInstance(Launcher launcher, Instance inst, string presetId, string? asId = null)
    {
        var src = Path.Combine(inst.Home, ".agent-presets", presetId);
        if (!Directory.Exists(src))
            throw new PadError("PA174", "no such preset in that instance", presetId, src,
                ["pad cli preset list <instance>"]);
        return Add(launcher, src, asId ?? presetId);
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(from, to);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
