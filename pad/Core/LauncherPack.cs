using System.IO;
using System.IO.Compression;

namespace Pad.Core;

/// <summary>
/// 带启动器的整合包（PCL 的 PackType 9）：zip 根目录或下一层有
/// <c>pack-agent-for DSH.exe</c>。这种包不装进当前实例，解到一个空文件夹。
/// </summary>
public static class LauncherPack
{
    public const string ExeName = "pack-agent-for DSH.exe";

    /// <summary>根目录，或只套了一层文件夹。再深就不算「这份包自带启动器」。</summary>
    public static bool IsLauncherEntry(string fullName)
    {
        var name = (fullName ?? "").Replace('\\', '/').TrimStart('/');
        if (name.Length == 0 || name.EndsWith('/')) return false;
        var parts = name.Split('/');
        if (parts.Length is < 1 or > 2) return false;
        return parts[^1].Equals(ExeName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Contains(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
                if (IsLauncherEntry(entry.FullName)) return true;
            return false;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static bool IsEmpty(string folder)
    {
        if (!Directory.Exists(folder)) return true;
        return !Directory.EnumerateFileSystemEntries(folder).Any();
    }

    /// <summary>解开之后的启动器：根上，或只进一层子目录。</summary>
    public static string? FindExe(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var direct = Path.Combine(folder, ExeName);
        if (File.Exists(direct)) return direct;
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            var nested = Path.Combine(dir, ExeName);
            if (File.Exists(nested)) return nested;
        }
        return null;
    }

    public static void Extract(string zipPath, string dest, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dest);
        var root = Path.GetFullPath(dest);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var rel = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (rel.Length == 0) continue;
            if (rel.Split('/').Contains(".."))
                throw new InvalidDataException("zip entry escapes the folder");
            var target = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!LaunchPolicy.PathUnder(target, root))
                throw new InvalidDataException("zip entry escapes the folder");
            if (rel.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }
}
