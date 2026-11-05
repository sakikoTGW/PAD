using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pad.Core;

/// <summary>
/// 直写 profile：不经过 <c>dsh plugin add</c>，因此对「由桌面端独占管理」的
/// desktop profile 同样有效。PAD 对所有 profile 一视同仁：改 package.json、
/// 跑 pnpm、按包自己的 <c>dsh.bundle.patch</c> 决定是否进 <c>dsh.profile.bundles</c>。
/// 改动前先备份，pnpm 失败就还原。
/// </summary>
public static class DirectProfile
{
    public static string PackageJson(string home, string profile) =>
        Path.Combine(home, "profiles", profile, "package.json");

    public static string ProfileDir(string home, string profile) =>
        Path.Combine(home, "profiles", profile);

    public static bool IsLocalPath(string spec) =>
        spec.Contains('/') || spec.Contains('\\') || spec.StartsWith(".", StringComparison.Ordinal)
        || (spec.Length > 1 && spec[1] == ':');

    /// <summary>把 spec 变成 package.json 里的依赖值：本地目录用 link:，npm 包用 ^版本。</summary>
    public static async Task<string> ResolveSpecAsync(string spec, string profileDir,
        IProgress<string>? log, CancellationToken ct)
    {
        if (spec.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
            return "link:" + spec["link:".Length..].Replace('\\', '/');

        if (IsLocalPath(spec))
            return "link:" + Path.GetFullPath(spec).Replace('\\', '/');

        if (spec.StartsWith("@", StringComparison.Ordinal) || spec.Contains('@', StringComparison.Ordinal))
        {
            // name@version
            var at = spec.LastIndexOf('@');
            if (at > 0 && at < spec.Length - 1)
                return spec;
        }

        var res = await Proc.Run("npm", $"view {spec} version", profileDir, log, ct,
            new Dictionary<string, string>()).ConfigureAwait(false);
        var version = res.Tail.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .LastOrDefault(s => s.Length > 0 && char.IsDigit(s[0]))
            ?? "";
        return version.Length > 0 ? "^" + version : "*";
    }

    /// <summary>装一个包（依赖 + 需要时进 bundles）。返回是否改了 bundles。</summary>
    public static async Task<bool> InstallAsync(Launcher launcher, Instance inst, string profile,
        string spec, IProgress<string>? log, CancellationToken ct)
    {
        var dir = ProfileDir(inst.Home, profile);
        Directory.CreateDirectory(dir);
        var pkgPath = PackageJson(inst.Home, profile);
        var backup = pkgPath + ".pad-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (File.Exists(pkgPath)) File.Copy(pkgPath, backup, overwrite: true);

        try
        {
            var dep = await ResolveSpecAsync(spec, dir, log, ct).ConfigureAwait(false);
            var root = JsonNode.Parse(File.Exists(pkgPath) ? File.ReadAllText(pkgPath) : "{}")?.AsObject()
                       ?? new JsonObject();
            var deps = root["dependencies"] as JsonObject ?? new JsonObject();
            root["dependencies"] = deps;

            // 依赖名：本地目录读它自己的 package.json，npm 包取 @ 前面的部分。
            string name;
            if (dep.StartsWith("link:", StringComparison.OrdinalIgnoreCase))
            {
                var target = dep["link:".Length..].Replace('/', '\\');
                var targetPkg = Path.Combine(target, "package.json");
                name = File.Exists(targetPkg)
                    ? (JsonNode.Parse(File.ReadAllText(targetPkg))?["name"]?.GetValue<string>() ?? "")
                    : Path.GetFileName(target);
                if (string.IsNullOrWhiteSpace(name))
                    throw new PadError("PA150", "that folder has no package.json name", spec, target, ["检查目录"]);
            }
            else
            {
                name = dep.Contains('@', StringComparison.Ordinal) ? dep[..dep.LastIndexOf('@')] : dep;
            }

            deps[name] = dep;
            File.WriteAllText(pkgPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

            log?.Report($"pnpm install（{profile}）…");
            var env = new Dictionary<string, string> { ["DSH_HOME"] = inst.Home };
            var res = await Proc.Run("pnpm", "install", dir, log, ct, env).ConfigureAwait(false);
            if (res.ExitCode != 0)
                throw new PadError("PA151", "pnpm install failed", $"{inst.Id}/{profile}", res.Tail,
                    ["看 pnpm 输出", "确认网络/包名"]);

            // 包自己声明了 bundle patch 才进 bundles；普通插件只留在 dependencies。
            var bundle = PackageDeclaresBundle(Path.Combine(dir, "node_modules", name));
            if (bundle is not null && AddBundle(root, name))
                File.WriteAllText(pkgPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return bundle is not null;
        }
        catch
        {
            if (File.Exists(backup)) File.Copy(backup, pkgPath, overwrite: true);
            throw;
        }
    }

    /// <summary>卸一个包：依赖、bundles、node_modules 一起清。</summary>
    public static async Task RemoveAsync(Launcher launcher, Instance inst, string profile,
        string name, IProgress<string>? log, CancellationToken ct)
    {
        var dir = ProfileDir(inst.Home, profile);
        var pkgPath = PackageJson(inst.Home, profile);
        var backup = pkgPath + ".pad-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        if (File.Exists(pkgPath)) File.Copy(pkgPath, backup, overwrite: true);

        try
        {
            var root = JsonNode.Parse(File.Exists(pkgPath) ? File.ReadAllText(pkgPath) : "{}")?.AsObject()
                       ?? new JsonObject();
            if (root["dependencies"] is JsonObject deps) deps.Remove(name);
            RemoveBundle(root, name);
            File.WriteAllText(pkgPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");

            var env = new Dictionary<string, string> { ["DSH_HOME"] = inst.Home };
            var res = await Proc.Run("pnpm", "install", dir, log, ct, env).ConfigureAwait(false);
            if (res.ExitCode != 0)
                throw new PadError("PA151", "pnpm install failed", $"{inst.Id}/{profile}", res.Tail,
                    ["看 pnpm 输出"]);
        }
        catch
        {
            if (File.Exists(backup)) File.Copy(backup, pkgPath, overwrite: true);
            throw;
        }
    }

    static string? PackageDeclaresBundle(string packageDir)
    {
        try
        {
            var pkg = Path.Combine(packageDir, "package.json");
            if (!File.Exists(pkg)) return null;
            var root = JsonNode.Parse(File.ReadAllText(pkg));
            var dsh = root?["dsh"];
            return dsh?["bundle"]?["patch"] is not null ? packageDir : null;
        }
        catch { return null; }
    }

    static bool AddBundle(JsonObject root, string name)
    {
        var bundles = root["dsh"]?["profile"]?["bundles"] as JsonArray;
        if (bundles is null)
        {
            var dsh = root["dsh"] as JsonObject ?? new JsonObject();
            root["dsh"] = dsh;
            var prof = dsh["profile"] as JsonObject ?? new JsonObject();
            dsh["profile"] = prof;
            bundles = new JsonArray();
            prof["bundles"] = bundles;
        }
        if (bundles.Any(x => string.Equals(x?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
            return false;
        bundles.Add(name);
        return true;
    }

    static void RemoveBundle(JsonObject root, string name)
    {
        if (root["dsh"]?["profile"]?["bundles"] is not JsonArray bundles) return;
        for (var i = bundles.Count - 1; i >= 0; i--)
            if (string.Equals(bundles[i]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                bundles.RemoveAt(i);
    }
}
