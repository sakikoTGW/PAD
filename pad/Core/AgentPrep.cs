using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pad.Core;

public sealed class AgentPrepResult
{
    public const string SchemaName = "pad.agent-prep/v1";

    [JsonPropertyName("schema")] public string Schema { get; init; } = SchemaName;
    [JsonPropertyName("instance")] public string Instance { get; init; } = "";
    [JsonPropertyName("profile")] public string Profile { get; init; } = "";
    [JsonPropertyName("recipe")] public string Recipe { get; init; } = "";
    [JsonPropertyName("profileCreated")] public bool ProfileCreated { get; init; }
    [JsonPropertyName("hmrChanged")] public bool HmrChanged { get; init; }
    [JsonPropertyName("hmrInstalled")] public bool HmrInstalled { get; init; }
    [JsonPropertyName("hmrMounted")] public bool HmrMounted { get; init; }
    [JsonPropertyName("checkpoint")] public string Checkpoint { get; init; } = "";
    [JsonPropertyName("applied")] public List<string> Applied { get; init; } = [];
    [JsonPropertyName("failedSpec")] public string? FailedSpec { get; init; }
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("restartRequired")] public bool RestartRequired { get; init; }
    [JsonPropertyName("hot")] public bool Hot { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

/// <summary>
/// One command that prepares an "agent" — an instance + profile — end to end:
/// create the profile from a recipe when missing, turn the official hot reload on,
/// then apply the requested plugins as one batch. Everything below this is the same
/// code the individual commands use; this only orders them and reports one verdict.
/// </summary>
public static class AgentPrep
{
    public static async Task<AgentPrepResult> RunAsync(Launcher launcher, Instance inst, string profile,
        string recipeKind, IReadOnlyList<string> specs, bool wantHot,
        IProgress<string>? log, CancellationToken ct)
    {
        ValidateRecipe(recipeKind);        var created = false;
        if (!launcher.Profiles(inst).Any(p => p.Name == profile))
        {
            var recipe = Cli.Recipe(recipeKind, inst.Dsh.Version);
            log?.Report($"profile {profile} 不存在，按 {recipeKind} 配方创建：{string.Join(", ", recipe)}");
            await launcher.AddBundles(inst, profile, recipe, log, ct);
            created = true;
        }

        var hmrChanged = await HmrProvision.EnsureAsync(launcher, inst, profile, log, ct);

        HotBatchResult? batch = null;
        if (specs.Count > 0)
            batch = await PluginCommands.PreloadAsync(launcher, inst, profile, specs, log, ct, wantHot);

        var cap = HotCapabilityProbe.Detect(launcher, inst, profile, refresh: true);
        var restart = created || hmrChanged || (batch?.RestartRequired ?? false);
        var ok = batch?.Ok ?? true;
        var message = ok
            ? $"预处理完成：{(created ? "新建 profile、" : "")}{(hmrChanged ? "开启热重载、" : "")}{(batch is null ? "" : $"{batch.Specs.Count} 个插件、")}{(restart ? "需重启一次生效" : "已生效")}"
            : batch!.Message;

        return new AgentPrepResult
        {
            Instance = inst.Id,
            Profile = profile,
            Recipe = recipeKind,
            ProfileCreated = created,
            HmrChanged = hmrChanged,
            HmrInstalled = HmrProvision.IsInstalled(inst, profile),
            HmrMounted = HmrProvision.IsMounted(inst.Home, profile),
            Checkpoint = batch?.Checkpoint ?? "",
            Applied = batch?.Specs ?? [],
            FailedSpec = batch?.FailedSpec,
            Ok = ok,
            RestartRequired = restart,
            Hot = cap.HotAllowed,
            Reason = cap.Reason,
            Message = message,
        };
    }

    static void ValidateRecipe(string kind)
    {
        if (kind is not ("web" or "tui" or "gateway"))
            throw new PadError("PA204", "unknown recipe", kind, null, ["web | tui | gateway"]);
    }

    public static string ToJson(AgentPrepResult result) => JsonSerializer.Serialize(result, Pretty);

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
}
