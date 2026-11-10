using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pad.Core;

/// <summary>Where PAD installs dsh-sisyphus and the agent preset package from. Remembered under
/// <c>&lt;launcher&gt;/library/sisyphus.json</c> so later setups need no arguments.</summary>
public sealed class SisyphusSources
{
    [JsonPropertyName("sisyphus")] public string? Sisyphus { get; set; }
    [JsonPropertyName("preset")] public string? Preset { get; set; }
}

/// <summary>A top-level row of a <c>--dump-config</c> dump.</summary>
public sealed record ConfigRow(string Id, bool Disabled);

public sealed class SisyphusRowCheck
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("present")] public bool Present { get; init; }
    [JsonPropertyName("disabled")] public bool Disabled { get; init; }
}

public sealed class SisyphusPackageCheck
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("dependency")] public bool Dependency { get; init; }
    [JsonPropertyName("bundle")] public bool Bundle { get; init; }
    [JsonPropertyName("rows")] public List<SisyphusRowCheck> Rows { get; init; } = [];
}

public sealed class SisyphusStatus
{
    [JsonPropertyName("schema")] public string Schema { get; init; } = "pad.sisyphus-status/v1";
    [JsonPropertyName("instance")] public string Instance { get; init; } = "";
    [JsonPropertyName("profile")] public string Profile { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("packages")] public List<SisyphusPackageCheck> Packages { get; init; } = [];
    [JsonPropertyName("presets")] public List<string> Presets { get; init; } = [];
    [JsonPropertyName("defaultPreset")] public string? DefaultPreset { get; init; }
    [JsonPropertyName("dataPath")] public string DataPath { get; init; } = "";
    [JsonPropertyName("projects")] public int? Projects { get; init; }
    [JsonPropertyName("checkpoint")] public string? Checkpoint { get; init; }
    [JsonPropertyName("ready")] public bool Ready { get; init; }
    [JsonPropertyName("problems")] public List<string> Problems { get; init; } = [];
}

/// <summary>
/// The experimental Sisyphus kit: dsh-sisyphus (project workspaces, in-DSH) plus an agent preset
/// bundle, installed as ordinary profile bundles. PAD owns only the install-time side; which
/// preset a project uses is Sisyphus' business. Every claim of "installed" is backed by the
/// pinned release's own <c>--dump-config</c>, never by the fact that pnpm exited 0.
/// </summary>
public static class Sisyphus
{
    public const string Experiment = "sisyphus";
    public const string DefaultPackage = "dsh-sisyphus";

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    static readonly Regex RowLine = new(@"^(\s*)- id:\s*(\S+)", RegexOptions.Compiled);
    static readonly Regex DisabledLine = new(@"^\s{2}disabled:\s*true\s*$", RegexOptions.Compiled);

    public static string SourcesPath(Launcher launcher) =>
        Path.Combine(launcher.Root, "library", "sisyphus.json");

    public static SisyphusSources LoadSources(Launcher launcher)
    {
        var path = SourcesPath(launcher);
        if (!File.Exists(path)) return new SisyphusSources();
        try { return JsonSerializer.Deserialize<SisyphusSources>(File.ReadAllText(path)) ?? new SisyphusSources(); }
        catch (JsonException) { return new SisyphusSources(); }
    }

    public static void SaveSources(Launcher launcher, SisyphusSources sources)
    {
        var path = SourcesPath(launcher);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(sources, Pretty), new UTF8Encoding(false));
    }

    /// <summary>dsh-sisyphus keeps its project store under <c>$DSH_HOME/sisyphus</c>.</summary>
    public static string DataPath(string home) => Path.Combine(home, "sisyphus", "projects.json");

    public static int? ProjectCount(string home)
    {
        var path = DataPath(home);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?["projects"] is JsonArray list ? list.Count : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Row ids a bundle patch adds through <c>- insert:</c>. Rows it merely edits
    /// (for example disabling a shipped row) are not the package's own and are skipped.</summary>
    public static List<string> InsertedRowIds(string patchText)
    {
        var ids = new List<string>();
        int? insertIndent = null, rowIndent = null;
        foreach (var raw in (patchText ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var body = line.TrimStart();
            if (body.Length == 0 || body.StartsWith('#')) continue;
            var indent = line.Length - body.Length;
            if (body == "- insert:")
            {
                insertIndent = indent;
                rowIndent = null;
                continue;
            }
            if (insertIndent is { } top && indent <= top) insertIndent = null;
            if (insertIndent is null) continue;
            var m = RowLine.Match(line);
            if (!m.Success) continue;
            rowIndent ??= indent;
            if (indent == rowIndent) ids.Add(Unquote(m.Groups[2].Value));
        }
        return ids.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Top-level rows of a dump and whether each one is disabled.</summary>
    public static List<ConfigRow> TopLevelRows(string dump)
    {
        var rows = new List<ConfigRow>();
        string? id = null;
        var disabled = false;
        foreach (var raw in (dump ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var m = RowLine.Match(line);
            if (m.Success && m.Groups[1].Value.Length == 0)
            {
                if (id is not null) rows.Add(new ConfigRow(id, disabled));
                id = Unquote(m.Groups[2].Value);
                disabled = false;
                continue;
            }
            if (id is not null && DisabledLine.IsMatch(line)) disabled = true;
        }
        if (id is not null) rows.Add(new ConfigRow(id, disabled));
        return rows;
    }

    /// <summary>Package name a spec will land under, when it can be known before installing:
    /// a folder's own package.json, or the npm name in <c>name[@range]</c>. Tarballs return null.</summary>
    public static string? NameForSpec(string spec)
    {
        var s = (spec ?? "").Trim().Trim('"');
        if (s.Length == 0) return null;
        if (Directory.Exists(s))
        {
            var pkg = Path.Combine(s, "package.json");
            if (!File.Exists(pkg)) return null;
            try { return JsonNode.Parse(File.ReadAllText(pkg))?["name"]?.GetValue<string>(); }
            catch (JsonException) { return null; }
        }
        if (File.Exists(s) || s.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return null;
        var at = s.LastIndexOf('@');
        return at > 0 ? s[..at] : s;
    }

    /// <summary>Rows an installed package inserts, read from its own <c>dsh.bundle.patch</c>.</summary>
    public static List<string> RowsOfInstalled(string home, string profile, string name)
    {
        var dir = Path.Combine(home, "profiles", profile, "node_modules", name.Replace('/', Path.DirectorySeparatorChar));
        var pkg = Path.Combine(dir, "package.json");
        if (!File.Exists(pkg)) return [];
        try
        {
            var patch = JsonNode.Parse(File.ReadAllText(pkg))?["dsh"]?["bundle"]?["patch"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(patch)) return [];
            var file = Path.GetFullPath(Path.Combine(dir, patch));
            return File.Exists(file) ? InsertedRowIds(File.ReadAllText(file)) : [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    /// <summary>A local folder must be a package that declares <c>dsh.bundle</c>; otherwise DSH
    /// would install it as a plain dependency and nothing would show up.</summary>
    public static void RequireBundleFolder(string spec, string role)
    {
        var s = (spec ?? "").Trim().Trim('"');
        if (!Directory.Exists(s)) return;
        var pkg = Path.Combine(s, "package.json");
        JsonNode? root = null;
        try { if (File.Exists(pkg)) root = JsonNode.Parse(File.ReadAllText(pkg)); }
        catch (JsonException) { /* reported below */ }
        if (root?["dsh"]?["bundle"]?["patch"] is null)
            throw new PadError("PA181", $"the {role} folder is not a bundle package", s,
                "package.json has no dsh.bundle.patch, so DSH would install it as a plain dependency",
                ["给的目录要有 package.json 和 \"dsh\": { \"bundle\": { \"patch\": \"./cordis.patch.yml\" } }"]);
    }

    static Dictionary<string, string> Dependencies(string home, string profile)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = DirectProfile.PackageJson(home, profile);
        if (!File.Exists(path)) return map;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path))?["dependencies"] is JsonObject deps)
                foreach (var (k, v) in deps) map[k] = v?.ToString() ?? "";
        }
        catch (JsonException) { /* an unreadable package.json has no dependencies we can trust */ }
        return map;
    }

    static List<string> Bundles(string home, string profile)
    {
        var path = DirectProfile.PackageJson(home, profile);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?["dsh"]?["profile"]?["bundles"] is JsonArray arr
                ? arr.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList()
                : [];
        }
        catch (JsonException) { return []; }
    }

    static List<string> ExpectedNames(SisyphusSources sources)
    {
        var names = new List<string>();
        names.Add(NameForSpec(sources.Sisyphus ?? "") ?? DefaultPackage);
        if (NameForSpec(sources.Preset ?? "") is { } preset) names.Add(preset);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Read-only facts: is the kit in this profile, and does the release agree.</summary>
    public static async Task<SisyphusStatus> StatusAsync(Launcher launcher, Instance inst, string profile,
        CancellationToken ct, IReadOnlyList<string>? names = null, string? checkpoint = null)
    {
        names ??= ExpectedNames(LoadSources(launcher));
        var problems = new List<string>();
        var deps = Dependencies(inst.Home, profile);
        var bundles = Bundles(inst.Home, profile);

        var (dump, error) = await AgentPresets.DumpAsync(launcher, inst.Home, inst.Dsh.Version,
            inst.Workspace?.Path ?? "", profile, ct);
        if (dump is null) problems.Add("读不到最终配置：" + error);
        var rows = TopLevelRows(dump ?? "").ToDictionary(r => r.Id, StringComparer.Ordinal);

        var packages = new List<SisyphusPackageCheck>();
        foreach (var name in names)
        {
            var checks = RowsOfInstalled(inst.Home, profile, name).Select(id => new SisyphusRowCheck
            {
                Id = id,
                Present = rows.ContainsKey(id),
                Disabled = rows.TryGetValue(id, out var r) && r.Disabled,
            }).ToList();
            var pkg = new SisyphusPackageCheck
            {
                Name = name,
                Dependency = deps.ContainsKey(name),
                Bundle = bundles.Contains(name, StringComparer.OrdinalIgnoreCase),
                Rows = checks,
            };
            packages.Add(pkg);
            if (!pkg.Dependency) problems.Add($"{name} 没装进 profile {profile}");
            else if (!pkg.Bundle) problems.Add($"{name} 只是普通依赖，没进 dsh.profile.bundles");
            else if (pkg.Rows.Count == 0) problems.Add($"{name} 的 bundle patch 里没有 insert 行，无从校验");
            if (dump is not null)
                foreach (var c in pkg.Rows.Where(c => !c.Present || c.Disabled))
                    problems.Add(c.Present ? $"{name} 的 {c.Id} 行被停用了" : $"最终配置里没有 {name} 的 {c.Id} 行");
        }

        var probe = dump is null ? null : AgentPresets.Parse(dump);
        return new SisyphusStatus
        {
            Instance = inst.Id,
            Profile = profile,
            Version = inst.Dsh.Version ?? "",
            Packages = packages,
            Presets = probe?.Presets.Select(p => p.Id).ToList() ?? [],
            DefaultPreset = probe?.DefaultId,
            DataPath = DataPath(inst.Home),
            Projects = ProjectCount(inst.Home),
            Checkpoint = checkpoint,
            Ready = problems.Count == 0,
            Problems = problems,
        };
    }

    /// <summary>
    /// Install the kit into one profile. A checkpoint is taken first; if DSH refuses the
    /// packages or the merged configuration does not contain every row they insert, the
    /// declaration layer is restored and PA182 names what was missing.
    /// </summary>
    public static async Task<SisyphusStatus> SetupAsync(Launcher launcher, Instance inst, string profile,
        SisyphusSources sources, IProgress<string>? log, CancellationToken ct)
    {
        var specs = new List<(string Spec, string Role)>();
        if (!string.IsNullOrWhiteSpace(sources.Sisyphus)) specs.Add((Normalize(sources.Sisyphus), "sisyphus"));
        if (!string.IsNullOrWhiteSpace(sources.Preset)) specs.Add((Normalize(sources.Preset), "preset"));
        if (specs.Count == 0)
            throw new PadError("PA180", "no Sisyphus source to install from", SourcesPath(launcher), null,
                ["pad cli sisyphus setup <instance> --sisyphus <目录|tgz|npm 包> [--preset <目录|tgz|npm 包>]"]);
        foreach (var (spec, role) in specs) RequireBundleFolder(spec, role);

        var before = Dependencies(inst.Home, profile);
        var ck = Checkpoints.Create(launcher, inst, profile, "sisyphus setup");
        log?.Report($"存档点 {ck.Id}");

        try
        {
            await launcher.AddBundles(inst, profile, specs.Select(s => Quote(s.Spec)), log, ct);
        }
        catch (Exception ex) when (ex is PadError or IOException)
        {
            Checkpoints.Restore(launcher, inst, ck.Id);
            throw new PadError("PA182", "the Sisyphus kit did not install; the profile was rolled back",
                $"instance `{inst.Id}` / profile `{profile}`", PadError.Describe(ex),
                [$"pad cli checkpoint list {inst.Id}", "看上面的 pnpm 输出"]);
        }

        var after = Dependencies(inst.Home, profile);
        var names = specs.Select(s => NameForSpec(s.Spec)).Where(n => n is not null).Select(n => n!).ToList();
        names.AddRange(after.Where(kv => !before.TryGetValue(kv.Key, out var v) || v != kv.Value)
            .Select(kv => kv.Key)
            .Where(k => !k.Contains("dsh-hmr", StringComparison.OrdinalIgnoreCase)));
        names = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var status = await StatusAsync(launcher, inst, profile, ct, names, ck.Id);
        if (!status.Ready)
        {
            Checkpoints.Restore(launcher, inst, ck.Id);
            throw new PadError("PA182", "the Sisyphus kit did not activate; the profile was rolled back",
                $"instance `{inst.Id}` / profile `{profile}`", string.Join("；", status.Problems),
                ["检查包的 cordis.patch.yml 与这个 DSH 版本是否匹配", $"pad cli sisyphus status {inst.Id} --profile {profile}"]);
        }

        var saved = LoadSources(launcher);
        foreach (var (spec, role) in specs)
            if (role == "sisyphus") saved.Sisyphus = spec;
            else saved.Preset = spec;
        SaveSources(launcher, saved);
        return status;
    }

    static string Normalize(string spec)
    {
        var s = spec.Trim().Trim('"');
        return Directory.Exists(s) || File.Exists(s) ? Path.GetFullPath(s) : s;
    }

    static string Quote(string spec) =>
        spec.IndexOfAny([' ', '\t']) >= 0 || !spec.All(c => c < 128) ? "\"" + spec.Replace("\"", "") + "\"" : spec;

    static string Unquote(string value) => value.Trim().Trim('"', '\'');
}
