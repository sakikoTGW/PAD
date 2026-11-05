using System.IO;
using System.Linq;

namespace Pad.Core;

/// <summary>
/// Turns the official hot reload on for a profile at provisioning time.
///
/// <c>@deepseek-ai/dsh-hmr</c> ships as a plain cordis plugin, not a bundle: adding it
/// through <c>dsh plugin add</c> only installs the dependency (DSH itself warns
/// "declares no dsh.bundle"), so the plugin never mounts and the capability probe keeps
/// reporting <c>hmr=absent</c>. Two things are needed, and this class does both:
/// install the dependency, then add the loader row to the profile's own patch layer.
/// The row is written as a marked block, so a user's other patch entries survive.
/// </summary>
public static class HmrProvision
{
    public const string Begin = "# >>> pad hmr (managed) >>>";
    public const string End = "# <<< pad hmr <<<";

    public static string PatchPath(string home, string profile) =>
        Path.Combine(home, "profiles", profile, "cordis.patch.yml");

    public static bool IsMounted(string home, string profile)
    {
        try
        {
            var path = PatchPath(home, profile);
            return File.Exists(path)
                && File.ReadAllText(path).Contains("id: hmr", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static bool IsInstalled(Instance inst, string profile) =>
        File.Exists(Path.Combine(inst.Home, "profiles", profile, "node_modules",
            "@deepseek-ai", "dsh-hmr", "package.json"));

    /// <summary>Add the loader row. Keeps the file a single YAML sequence: a bare
    /// <c>[]</c> is replaced, anything else is appended after the existing entries.</summary>
    public static void Mount(string home, string profile)
    {
        var dir = Path.Combine(home, "profiles", profile);
        Directory.CreateDirectory(dir);
        var path = PatchPath(home, profile);
        var text = File.Exists(path) ? File.ReadAllText(path) : "";
        if (text.Contains("id: hmr", StringComparison.Ordinal)) return;

        var block = string.Join('\n', Begin, "- id: hmr", "  name: '@deepseek-ai/dsh-hmr'", End);
        var body = text.TrimEnd();
        if (body.EndsWith("[]", StringComparison.Ordinal))
        {
            var head = body[..^2].TrimEnd();
            text = (head.Length > 0 ? head + "\n" : "") + block + "\n";
        }
        else if (body.Length == 0)
        {
            text = block + "\n";
        }
        else
        {
            text = body + "\n\n" + block + "\n";
        }

        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Install the plugin if missing and add the loader row. Returns true when
    /// something changed, i.e. when one cold restart is still required to take effect.</summary>
    public static async Task<bool> EnsureAsync(Launcher launcher, Instance inst, string profile,
        IProgress<string>? log, CancellationToken ct)
    {
        var changed = false;
        if (!IsInstalled(inst, profile))
        {
            log?.Report($"开启官方热重载：装 {Cli.HmrSpec(inst.Dsh.Version)}");
            // Installs the dependency only; the warning about "no dsh.bundle" is expected
            // and harmless — the loader row below is what actually mounts it.
            await launcher.AddBundles(inst, profile, [Cli.HmrSpec(inst.Dsh.Version)], log, ct);
            changed = true;
        }
        if (!IsMounted(inst.Home, profile))
        {
            Mount(inst.Home, profile);
            changed = true;
            log?.Report("官方热重载：已写入 cordis.patch.yml 的 hmr 行");
        }
        return changed;
    }
}
