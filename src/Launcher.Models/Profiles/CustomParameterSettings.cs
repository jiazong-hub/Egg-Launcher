using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Launcher.Models.Profiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomParameter
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public bool NoValue { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CustomParameterSettings
{
    // Lists deliberately retain duplicate rows so the editor can explain and repair them.
    public IReadOnlyList<CustomParameter> Llama { get; init; } = [];
    public IReadOnlyList<CustomParameter> Codex { get; init; } = [];
    [JsonIgnore]
    public bool HasIntent => Llama.Count != 0 || Codex.Count != 0;
}

public static partial class CustomParameterRules
{
    // This registry includes defaults and automatic fields, not just arguments emitted for a run.
    private static readonly HashSet<string> FixedLlama = new((
        "model alias host port api-key api-key-file ssl-key-file ssl-cert-file hf-repo hf-file hf-token " +
        "models-dir models-preset models-max models-autoload load-on-startup sleep-idle-seconds " +
        "ctx-size n-gpu-layers device flash-attn cache-type-k cache-type-v parallel jinja no-jinja " +
        "batch-size ubatch-size chat-template chat-template-file chat-template-kwargs reasoning reasoning-budget " +
        "context-shift no-context-shift keep threads threads-batch kv-offload no-kv-offload load-mode lazy-mode " +
        "fit fit-target fit-ctx n-cpu-ffn split-mode tensor-split main-gpu numa swa-full ctx-checkpoints swa-checkpoints " +
        "repack no-repack op-offload no-op-offload no-host rope-scaling rope-scale rope-freq-base rope-freq-scale " +
        "yarn-orig-ctx yarn-ext-factor yarn-attn-factor yarn-beta-slow yarn-beta-fast override-tensor cpu-moe n-cpu-moe " +
        "mmproj no-mmproj mmproj-offload no-mmproj-offload mmproj-device image-min-tokens image-max-tokens mtmd-batch-max-tokens " +
        "spec-type spec-draft-model spec-draft-n-max spec-draft-n-min spec-draft-p-min spec-draft-p-split " +
        "spec-draft-backend-sampling no-spec-draft-backend-sampling spec-draft-ngl spec-draft-device spec-draft-type-k " +
        "spec-draft-type-v spec-draft-threads spec-draft-threads-batch spec-draft-n-ubatch spec-draft-sampling " +
        "moe-cache-mib cache-ram cache-prompt no-cache-prompt cache-idle-slots no-cache-idle-slots checkpoint-min-step " +
        "kv-unified no-kv-unified backend-sampling lookup-cache-static lookup-cache-dynamic spec-default " +
        "model-url mmproj-url api-prefix hf-repo-draft hf-file-draft hf-repo-mmproj model-vocoder hf-repo-vocoder " +
        "rpc embedding embeddings reranking rerank webui no-webui path slot-save-path slots metrics props " +
        "mmap no-mmap mlock no-mlock version help list-devices list-models")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    // Whole launcher-managed namespaces are reserved, including their ancestors/descendants.
    public static IReadOnlyList<string> FixedCodex { get; } = Array.AsReadOnly(new[]
    {
        "model", "model_provider", "model_providers", "model_catalog_json", "openai_base_url", "profile", "profiles",
        "model_context_window", "model_auto_compact_token_limit", "model_auto_compact_token_limit_scope", "tool_output_token_limit",
        "model_reasoning_effort", "plan_mode_reasoning_effort", "model_reasoning_summary", "show_raw_agent_reasoning",
        "model_supports_reasoning_summaries", "model_verbosity", "service_tier", "approval_policy", "approvals_reviewer",
        "default_permissions", "sandbox_mode", "sandbox_workspace_write", "shell_environment_policy", "developer_instructions",
        "forced_login_method", "forced_chatgpt_workspace_id", "chatgpt_base_url", "cli_auth_credentials_store",
        "experimental_bearer_token", "api_key", "auth", "permissions",
    });

    public static string LlamaKey(string name)
    {
        // CLI option spelling is case-sensitive (-c and -C are different commands).
        var key = name.TrimStart('-');
        return Aliases.GetValueOrDefault(key, key);
    }

    private static string Family(string key) => key.StartsWith("no-", StringComparison.Ordinal) ? key[3..] : key;
    public static bool Overlaps(string left, string right) => left == right || left.StartsWith(right + ".", StringComparison.Ordinal) || right.StartsWith(left + ".", StringComparison.Ordinal);

    private static bool IsReservedLlama(string key) => FixedLlama.Any(k => Family(LlamaKey(k)) == key)
        || key.StartsWith("ngram-", StringComparison.Ordinal) || key.StartsWith("spec-ngram-", StringComparison.Ordinal)
        || key.StartsWith("spec-synth", StringComparison.Ordinal);

    private static bool UnsafeLlamaValue(CustomParameter row) => row.Value.Any(char.IsControl)
        || row.Value.IndexOfAny(['"', '#', ';']) >= 0 || row.Value.Trim() != row.Value;

    // File-generation safety, like the existing BAT quoting and ExtraArguments guards.
    // This does not repeat the editor's duplicate/TOML/type checks at startup.
    public static void EnsureLlamaEmissionSafe(CustomParameter row)
    {
        if (row is null || row.Name is null || row.Value is null || !LlamaNameRegex().IsMatch(row.Name)
            || IsReservedLlama(Family(LlamaKey(row.Name))) || UnsafeLlamaValue(row))
            throw new InvalidDataException("无法安全生成自定 llama.cpp 参数 / Cannot safely generate custom llama.cpp arguments.");
    }

    public static IReadOnlyList<string> Validate(CustomParameterSettings? settings, IEnumerable<string>? extraKeys = null)
    {
        var errors = new List<string>();
        if (settings?.Llama is null || settings.Codex is null) return ["自定参数列表无效 / Invalid custom parameter lists."];
        ValidateRows(settings.Llama, false);
        ValidateRows(settings.Codex, true);
        return errors;

        void ValidateRows(IReadOnlyList<CustomParameter> rows, bool codex)
        {
            if (rows.Count > 128) { errors.Add("每组自定参数最多 128 行 / Maximum 128 rows per receiver."); return; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var extras = (extraKeys ?? []).Select(key => Family(LlamaKey(key))).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                var label = $"{(codex ? "Codex" : "llama.cpp")} #{i + 1}";
                if (row is null || row.Name is null || row.Value is null || row.Name.Length > 128 || row.Value.Length > 16384
                    || !(codex ? CodexNameRegex() : LlamaNameRegex()).IsMatch(row.Name))
                { errors.Add(label + "：参数名称格式无效 / Invalid parameter name."); continue; }
                var key = codex ? row.Name : Family(LlamaKey(row.Name));
                var reserved = codex ? FixedCodex.Any(k => Overlaps(k, key))
                    : IsReservedLlama(key) || extras.Contains(key);
                if (reserved) errors.Add(label + "：与固定或自动管理参数重复，请删除此行 / Conflicts with a managed parameter; delete this row.");
                if (seen.Any(k => codex ? Overlaps(k, key) : k == key)) errors.Add(label + "：命令重复，请删除重复行 / Duplicate command; delete the duplicate row.");
                seen.Add(key);
                if (row.Value.Any(char.IsControl) || (!codex && UnsafeLlamaValue(row))
                    || (row.NoValue && (codex || row.Value.Length != 0)) || (!row.NoValue && row.Value.Length == 0))
                    errors.Add(label + "：参数值格式无效 / Invalid value; use a value or an explicit llama.cpp valueless switch.");
            }
        }
    }

    [GeneratedRegex(@"^-{1,2}[A-Za-z][A-Za-z0-9-]*$")]
    private static partial Regex LlamaNameRegex();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*(\.[A-Za-z_][A-Za-z0-9_-]*)*$")]
    private static partial Regex CodexNameRegex();
}
