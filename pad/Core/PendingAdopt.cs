using System.IO;

namespace Pad.Core;

/// <summary>A discovered DSH home that is not yet an instance in this launcher.</summary>
public sealed record PendingHome(string Path, int Profiles, int Sessions, string Found);

/// <summary>
/// Startup sniff minus what this launcher already has. Does not write anything:
/// the window lists the result and asks which homes to adopt.
/// </summary>
public static class PendingAdopt
{
    public static List<PendingHome> List(
        IEnumerable<DshHome> sniffed,
        IEnumerable<string> adopted,
        IEnumerable<string> skipped)
    {
        var raw = new List<PendingHome>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var home in sniffed ?? [])
        {
            var path = Normalize(home.Path);
            if (path.Length == 0) continue;
            if (!seen.Add(Canonical(path))) continue;
            raw.Add(new PendingHome(path, home.Profiles.Count, home.Sessions, home.Found ?? ""));
        }

        var have = Index(adopted);
        var skip = Index(skipped);
        var list = new List<PendingHome>();
        foreach (var home in raw)
        {
            if (have.Any(h => LaunchPolicy.SamePath(h, home.Path))) continue;
            if (skip.Any(h => LaunchPolicy.SamePath(h, home.Path))) continue;
            if (IsUserProfile(home.Path)) continue;
            if (raw.Any(other => IsStrictParent(home.Path, other.Path))) continue;
            list.Add(home);
        }
        list.Sort((a, b) =>
        {
            var bySessions = b.Sessions.CompareTo(a.Sessions);
            if (bySessions != 0) return bySessions;
            var byProfiles = b.Profiles.CompareTo(a.Profiles);
            if (byProfiles != 0) return byProfiles;
            return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    public static string SuggestName(string path)
    {
        var full = Normalize(path);
        if (full.Length == 0) return "本机 DSH";
        var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leaf, ".dsh", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, "dsh", StringComparison.OrdinalIgnoreCase))
            return "本机 DSH";
        if (string.Equals(leaf, "home", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
            if (!string.IsNullOrWhiteSpace(parent)) return parent;
        }
        return string.IsNullOrWhiteSpace(leaf) ? "本机 DSH" : leaf;
    }

    static bool IsUserProfile(string path)
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return user.Length > 0 && LaunchPolicy.SamePath(path, user);
    }

    static bool IsStrictParent(string parent, string child)
    {
        var a = Canonical(parent);
        var b = Canonical(child);
        if (a.Length == 0 || b.Length == 0 || LaunchPolicy.SamePath(a, b)) return false;
        var prefix = a.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    static List<string> Index(IEnumerable<string> paths) =>
        (paths ?? []).Select(Normalize).Where(p => p.Length > 0).ToList();

    static string Canonical(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path.TrimEnd('\\', '/'); }
    }

    static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path.Trim()); }
        catch { return path.Trim(); }
    }
}
