using System.Globalization;
using System.Text.Json.Serialization;

namespace Launcher.Models.Profiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeParameterSettings
{
    public int? DraftMicroBatchSize { get; init; }
    public string? DraftSampling { get; init; }
    public int? MoeCacheMiB { get; init; }
    public int? CacheRamMiB { get; init; }
    public bool? CachePrompt { get; init; }
    public bool? CacheIdleSlots { get; init; }
    public int? CheckpointMinimumStep { get; init; }
    public bool? KvUnified { get; init; }
    public bool? BackendSampling { get; init; }

    [JsonIgnore]
    public bool HasIntent => this != new RuntimeParameterSettings();

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (DraftMicroBatchSize is < 0) errors.Add("--spec-draft-n-ubatch 必须为非负整数 / Must be non-negative.");
        if (DraftSampling is not (null or "greedy" or "probabilistic")) errors.Add("--spec-draft-sampling 只能是 greedy 或 probabilistic.");
        if (MoeCacheMiB is < 0) errors.Add("--moe-cache-mib 必须为非负整数 / Must be non-negative.");
        if (CacheRamMiB is < -1) errors.Add("--cache-ram 最小为 -1 / Minimum is -1.");
        if (CheckpointMinimumStep is < 0) errors.Add("--checkpoint-min-step 必须为非负整数 / Must be non-negative.");
        if (BackendSampling is false) errors.Add("--backend-sampling 仅支持默认或开启 / Only Default or On is supported.");
        if (CacheRamMiB is 0 && CacheIdleSlots is true)
            errors.Add("缓存容量为 0 时不能开启空闲槽缓存 / Idle-slot caching requires a nonzero cache capacity.");
        return errors;
    }

    public IEnumerable<KeyValuePair<string, string?>> Arguments(ModelProfile model)
    {
        if (model.MtpEnabled)
        {
            if (DraftMicroBatchSize is int batch) yield return new("spec-draft-n-ubatch", Number(batch));
            if (DraftSampling is not null) yield return new("spec-draft-sampling", DraftSampling);
        }
        if (model.ModelType == ModelType.MoE && MoeCacheMiB is int moe) yield return new("moe-cache-mib", Number(moe));
        if (CacheRamMiB is int ram) yield return new("cache-ram", Number(ram));
        if (CheckpointMinimumStep is int step) yield return new("checkpoint-min-step", Number(step));
        if (CachePrompt is bool prompt) yield return new(prompt ? "cache-prompt" : "no-cache-prompt", null);
        if (CacheIdleSlots is bool idle) yield return new(idle ? "cache-idle-slots" : "no-cache-idle-slots", null);
        if (KvUnified is bool unified) yield return new(unified ? "kv-unified" : "no-kv-unified", null);
        if (BackendSampling is true) yield return new("backend-sampling", null);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
