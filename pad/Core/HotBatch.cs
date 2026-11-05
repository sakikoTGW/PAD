using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pad.Core;

public sealed class HotBatchResult
{
    public const string SchemaName = "pad.hot-batch/v1";

    [JsonPropertyName("schema")] public string Schema { get; init; } = SchemaName;
    [JsonPropertyName("action")] public string Action { get; init; } = "add";
    [JsonPropertyName("instance")] public string Instance { get; init; } = "";
    [JsonPropertyName("profile")] public string Profile { get; init; } = "";
    /// <summary><c>hot</c> or <c>cold</c>.</summary>
    [JsonPropertyName("mode")] public string Mode { get; init; } = "cold";
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("restartRequired")] public bool RestartRequired { get; init; }
    [JsonPropertyName("restartReason")] public string? RestartReason { get; init; }
    [JsonPropertyName("hotAllowed")] public bool HotAllowed { get; init; }
    [JsonPropertyName("hotReason")] public string HotReason { get; init; } = "";
    [JsonPropertyName("checkpoint")] public string Checkpoint { get; init; } = "";
    [JsonPropertyName("specs")] public List<string> Specs { get; init; } = [];
    /// <summary>The spec whose apply/verify failed first; null when the whole batch landed.</summary>
    [JsonPropertyName("failedSpec")] public string? FailedSpec { get; init; }
    [JsonPropertyName("verify")] public List<PluginVerifyResult> Verify { get; init; } = [];
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>
/// Batch form of <see cref="HotReload"/>: one checkpoint for the whole list, one settle
/// window, then V1–V4 per spec. Any failure restores that single checkpoint, so a
/// half-applied batch never survives as "some plugins loaded".
/// </summary>
public static class HotBatch
{
    public static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan SettleAfterRow = TimeSpan.FromMilliseconds(1500);

    public static async Task<HotBatchResult> ApplyAsync(
        Launcher launcher, Instance inst, string profile, string action,
        IReadOnlyList<string> specs, bool wantHot, IProgress<string>? log, CancellationToken ct)
    {
        var list = specs.Select(s => (s ?? "").Trim())
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0)
            throw new PadError("PA204", "no plugin specs to apply", $"instance `{inst.Id}`", null,
                ["pad cli plugin preload <instance> <profile> <spec...>"]);

        var cap = HotCapabilityProbe.Detect(launcher, inst, profile, refresh: true);
        var useHot = wantHot && cap.HotAllowed;
        var checkpoint = Checkpoints.Create(launcher, inst, profile,
            $"plugin {action} batch({list.Count}) {string.Join(",", list)}");
        var pidBefore = useHot ? PluginVerify.LivePid(launcher, inst.Id, profile) : null;

        log?.Report(useHot
            ? $"hot batch: {action} {list.Count} specs (checkpoint {checkpoint.Id})"
            : $"cold batch: {action} {list.Count} specs (checkpoint {checkpoint.Id}; restart required)");

        var applied = new List<string>();
        var verifies = new List<PluginVerifyResult>();
        string? failedSpec = null;
        string? failure = null;

        try
        {
            foreach (var spec in list)
            {
                ct.ThrowIfCancellationRequested();
                await launcher.PluginOp(inst.Id, action, spec, log, ct, profile, useHot);
                applied.Add(spec);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failedSpec = list.ElementAtOrDefault(applied.Count);
            failure = PadError.Describe(ex);
        }

        if (failure is null && useHot)
        {
            await SettleAsync(launcher, inst, profile, list, ct);
            foreach (var spec in list)
            {
                var verify = await PluginVerify.RunAsync(launcher, inst, profile, spec,
                    PluginExpect.Present, null, pidBefore, ct);
                verifies.Add(verify);
                if (verify.Result == "pass") continue;
                failedSpec = spec;
                failure = "hot verify failed";
                break;
            }
        }

        if (failure is null)
        {
            if (useHot)
            {
                inst.Status = "ready";
                launcher.SaveInstance(inst);
            }
            return Save(launcher, inst, new HotBatchResult
            {
                Action = action,
                Instance = inst.Id,
                Profile = profile,
                Mode = useHot ? "hot" : "cold",
                Ok = true,
                RestartRequired = !useHot,
                RestartReason = useHot ? null : (wantHot ? $"not-hot: {cap.Reason}" : "cold-default"),
                HotAllowed = cap.HotAllowed,
                HotReason = cap.Reason,
                Checkpoint = checkpoint.Id,
                Specs = list,
                Verify = verifies,
                Message = useHot ? $"已热生效：{list.Count} 个" : $"已写入：{list.Count} 个，重启后生效",
            });
        }

        log?.Report($"batch failed at {failedSpec ?? "?"}; restoring checkpoint {checkpoint.Id}");
        var rollbackOk = true;
        try
        {
            Checkpoints.Restore(launcher, inst, checkpoint.Id);
        }
        catch (Exception ex)
        {
            rollbackOk = false;
            log?.Report("rollback failed: " + ex.Message);
        }

        return Save(launcher, inst, new HotBatchResult
        {
            Action = action,
            Instance = inst.Id,
            Profile = profile,
            Mode = useHot ? "hot" : "cold",
            Ok = false,
            RestartRequired = true,
            RestartReason = rollbackOk ? "batch-failed" : "rollback-failed",
            HotAllowed = cap.HotAllowed,
            HotReason = cap.Reason,
            Checkpoint = checkpoint.Id,
            Specs = list,
            FailedSpec = failedSpec,
            Verify = verifies,
            Message = rollbackOk
                ? $"批量失败于 {failedSpec ?? "安装"}，已回滚到 {checkpoint.Id}"
                : "批量失败且回滚失败，需人工处理",
        });
    }

    /// <summary>Wait until every row is present, then give the official HMR its settle window.</summary>
    static async Task SettleAsync(
        Launcher launcher, Instance inst, string profile, IReadOnlyList<string> specs, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + SettleTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (specs.All(s => PluginVerify.RowMatches(launcher, inst, profile, s))) break;
            try { await Task.Delay(300, ct); }
            catch (OperationCanceledException) { return; }
        }
        try { await Task.Delay(SettleAfterRow, ct); }
        catch (OperationCanceledException) { /* verify still runs */ }
    }

    static HotBatchResult Save(Launcher launcher, Instance inst, HotBatchResult result)
    {
        HotStates.Save(launcher, inst, new HotApplyResult
        {
            Action = result.Action,
            Instance = result.Instance,
            Profile = result.Profile,
            Spec = string.Join(",", result.Specs),
            Mode = result.Mode,
            Ok = result.Ok,
            RestartRequired = result.RestartRequired,
            RestartReason = result.RestartReason,
            HotAllowed = result.HotAllowed,
            HotReason = result.HotReason,
            Checkpoint = result.Checkpoint,
            Message = result.Message,
        });
        return result;
    }

    public static string ToJson(HotBatchResult result) => JsonSerializer.Serialize(result, Pretty);

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
}
