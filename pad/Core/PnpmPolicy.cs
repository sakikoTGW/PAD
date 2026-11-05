using System.IO;

namespace Pad.Core;

/// <summary>
/// pnpm 11 moved build approval into <c>pnpm-workspace.yaml</c>'s <c>allowBuilds</c> map,
/// and DSH writes a human placeholder there (<c>koffi: set this to true or false</c>).
/// That is not a boolean, so the first real install dies with
/// <c>ERR_PNPM_IGNORED_BUILDS</c> before any plugin lands. PAD's own .npmrc already opts
/// into builds, so rewriting the placeholder to <c>true</c> matches the stated intent.
/// </summary>
public static class PnpmPolicy
{
    public const string Placeholder = "set this to true or false";

    public static bool Normalize(string profileDir)
    {
        try
        {
            var path = Path.Combine(profileDir, "pnpm-workspace.yaml");
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            if (!text.Contains(Placeholder, StringComparison.Ordinal)) return false;
            var fixedText = text.Replace(Placeholder, "true", StringComparison.Ordinal);
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, fixedText);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }
}
