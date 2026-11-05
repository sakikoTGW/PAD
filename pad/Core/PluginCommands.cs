namespace Pad.Core;

/// <summary>
/// Single app-facing entry for plugin add/remove/update.
/// Views and CLI must go through here (→ <see cref="HotReload.ApplyAsync"/> →
/// <see cref="Launcher.PluginOp"/>); do not call <c>PluginOp</c>/<c>AddBundles</c>
/// from UI for one-off plugin mutations.
/// </summary>
public static class PluginCommands
{
    public static Task<HotApplyResult> AddAsync(
        Launcher launcher, Instance inst, string profile, string spec,
        IProgress<string>? log, CancellationToken ct, bool wantHot = false) =>
        ApplyAsync(launcher, inst, profile, "add", spec, log, ct, wantHot);

    public static Task<HotApplyResult> RemoveAsync(
        Launcher launcher, Instance inst, string profile, string spec,
        IProgress<string>? log, CancellationToken ct, bool wantHot = false) =>
        ApplyAsync(launcher, inst, profile, "remove", spec, log, ct, wantHot);

    public static Task<HotApplyResult> UpdateAsync(
        Launcher launcher, Instance inst, string profile, string spec,
        IProgress<string>? log, CancellationToken ct, bool wantHot = false) =>
        ApplyAsync(launcher, inst, profile, "update", spec, log, ct, wantHot);

    /// <summary>Agent preload: apply a whole batch through one checkpoint and one settle window.
    /// All-or-nothing — a failure restores the checkpoint instead of leaving a half-loaded set.</summary>
    public static Task<HotBatchResult> PreloadAsync(
        Launcher launcher, Instance inst, string profile, IReadOnlyList<string> specs,
        IProgress<string>? log, CancellationToken ct, bool wantHot = false) =>
        HotBatch.ApplyAsync(launcher, inst, profile, "add", specs, wantHot, log, ct);

    public static Task<HotApplyResult> ApplyAsync(
        Launcher launcher, Instance inst, string profile, string action, string spec,
        IProgress<string>? log, CancellationToken ct, bool wantHot = false)
    {
        if (action is not ("add" or "remove" or "update"))
            throw new ArgumentOutOfRangeException(nameof(action), action, "expected add|remove|update");
        return HotReload.ApplyAsync(launcher, inst, profile, action, spec, wantHot, log, ct);
    }
}
