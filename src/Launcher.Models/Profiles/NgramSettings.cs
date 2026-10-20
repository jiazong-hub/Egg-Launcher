using System.Globalization;
using System.Text.Json.Serialization;

namespace Launcher.Models.Profiles;

/// <summary>User intent only. Disabled strategies retain their settings; no capability proof is stored here.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record NgramSettings
{
    public bool Enabled { get; init; }
    public IReadOnlyList<string> Types { get; init; } = [];
    public IReadOnlyDictionary<string, int?> Parameters { get; init; } =
        NgramOptions.Parameters.ToDictionary(p => p.Key, _ => (int?)null, StringComparer.Ordinal);
    public string? StaticCachePath { get; init; }
    public string? DynamicCachePath { get; init; }
}

public sealed record NgramParameter(string Type, string Key, string Chinese, string English, int Minimum, int Maximum);

public static class NgramOptions
{
    public static bool HasIntent(NgramSettings settings) => settings.Enabled || settings.Types.Count > 0
        || settings.Parameters.Values.Any(value => value is not null)
        || settings.StaticCachePath is not null || settings.DynamicCachePath is not null;

    public static IReadOnlyList<string> Types { get; } = Array.AsReadOnly(new[]
    { "ngram-simple", "ngram-map-k", "ngram-map-k4v", "ngram-mod", "ngram-cache" });

    public static IReadOnlyList<NgramParameter> Parameters { get; } = Array.AsReadOnly(new[]
    {
        new NgramParameter("ngram-simple", "spec-ngram-simple-size-n", "匹配长度", "Lookup length", 1, 1024),
        new NgramParameter("ngram-simple", "spec-ngram-simple-size-m", "草稿长度", "Draft length", 1, 1024),
        new NgramParameter("ngram-simple", "spec-ngram-simple-min-hits", "最小命中次数", "Minimum hits", 1, ushort.MaxValue),
        new NgramParameter("ngram-map-k", "spec-ngram-map-k-size-n", "匹配长度", "Lookup length", 1, 1024),
        new NgramParameter("ngram-map-k", "spec-ngram-map-k-size-m", "草稿长度", "Draft length", 1, 1024),
        new NgramParameter("ngram-map-k", "spec-ngram-map-k-min-hits", "最小命中次数", "Minimum hits", 1, ushort.MaxValue),
        new NgramParameter("ngram-map-k4v", "spec-ngram-map-k4v-size-n", "匹配长度", "Lookup length", 1, 1024),
        new NgramParameter("ngram-map-k4v", "spec-ngram-map-k4v-size-m", "草稿长度", "Draft length", 1, 1024),
        new NgramParameter("ngram-map-k4v", "spec-ngram-map-k4v-min-hits", "最小命中次数", "Minimum hits", 1, ushort.MaxValue),
        new NgramParameter("ngram-mod", "spec-ngram-mod-n-match", "匹配长度", "Lookup length", 1, 1024),
        new NgramParameter("ngram-mod", "spec-ngram-mod-n-min", "最小草稿长度", "Minimum draft length", 0, 1024),
        new NgramParameter("ngram-mod", "spec-ngram-mod-n-max", "最大草稿长度", "Maximum draft length", 0, 1024),
    });

    public static bool IsManagedKey(string key) => key.StartsWith("spec-ngram-", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("spec-synth-", StringComparison.OrdinalIgnoreCase)
        || key is "lookup-cache-static" or "lookup-cache-dynamic" or "lcs" or "lcd" or "spec-default";

    public static IEnumerable<KeyValuePair<string, string?>> Arguments(NgramSettings settings)
    {
        if (!settings.Enabled) yield break;
        foreach (var parameter in Parameters)
            if (settings.Types.Contains(parameter.Type, StringComparer.Ordinal)
                && settings.Parameters.TryGetValue(parameter.Key, out var value) && value is int number)
                yield return new(parameter.Key, number.ToString(CultureInfo.InvariantCulture));
        if (settings.Types.Contains("ngram-cache", StringComparer.Ordinal))
        {
            if (settings.StaticCachePath is not null) yield return new("lookup-cache-static", settings.StaticCachePath);
            if (settings.DynamicCachePath is not null) yield return new("lookup-cache-dynamic", settings.DynamicCachePath);
        }
    }

    public static IReadOnlyList<string> Validate(NgramSettings? settings, string root, bool checkFiles = true)
    {
        var errors = new List<string>();
        if (settings?.Types is null || settings.Parameters is null)
            return ["n-gram 配置缺失 / Missing n-gram settings."];
        if (settings.Types.Any(t => !Types.Contains(t, StringComparer.Ordinal))
            || settings.Types.Distinct(StringComparer.Ordinal).Count() != settings.Types.Count)
            errors.Add("n-gram 类型无效或重复 / Invalid or duplicate n-gram types.");
        if (settings.Enabled && settings.Types.Count == 0)
            errors.Add("启用 n-gram 时请选择至少一种策略 / Select at least one n-gram strategy.");
        foreach (var pair in settings.Parameters)
        {
            var definition = Parameters.FirstOrDefault(p => p.Key == pair.Key);
            if (definition is null) errors.Add($"未知 n-gram 参数 / Unknown n-gram parameter: {pair.Key}");
            else if (pair.Value is int value && (value < definition.Minimum || value > definition.Maximum))
                errors.Add($"--{pair.Key}: {definition.Minimum} ~ {definition.Maximum}");
        }
        settings.Parameters.TryGetValue("spec-ngram-mod-n-min", out var minimum);
        settings.Parameters.TryGetValue("spec-ngram-mod-n-max", out var maximum);
        if (minimum is not null && maximum is not null && minimum > maximum)
            errors.Add("ngram-mod 最小草稿长度不能大于最大草稿长度 / Minimum draft length cannot exceed maximum.");
        foreach (var path in new[] { settings.StaticCachePath, settings.DynamicCachePath })
        {
            if (path is null) continue;
            try
            {
                var full = ResolveCachePath(root, path);
                if (checkFiles && settings.Enabled && settings.Types.Contains("ngram-cache", StringComparer.Ordinal))
                {
                    using var stream = File.OpenRead(full);
                    if (stream.Length == 0) throw new InvalidDataException("缓存文件为空 / Empty cache file.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { errors.Add($"n-gram 缓存不可用 / Cache unavailable: {path} ({ex.Message})"); }
        }
        return errors;
    }

    // These are token-ID statistics, not JSON or GGUF. Paths are per-configuration and never auto-created or overwritten.
    public static string ResolveCachePath(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\0', '\r', '\n', '"']) >= 0)
            throw new InvalidDataException("缓存路径无效 / Invalid cache path.");
        if (!Path.IsPathFullyQualified(path) && (Path.IsPathRooted(path) || path.Contains(':')))
            throw new InvalidDataException("请使用完整路径或相对运行库的路径 / Use an absolute or runtime-relative path.");
        return Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path));
    }

    public static IReadOnlyList<string> ValidateRuntime(NgramSettings settings, bool mtpEnabled, IReadOnlySet<string>? capabilities)
    {
        if (!settings.Enabled) return [];
        var errors = new List<string>();
        foreach (var type in settings.Types.Concat(mtpEnabled ? new[] { "draft-mtp" } : []))
            if (capabilities?.Contains("spec-type:" + type) != true)
                errors.Add($"无法确认运行库支持策略 / Runtime strategy support unconfirmed: {type}");
        foreach (var argument in Arguments(settings))
            if (capabilities?.Contains(argument.Key) != true)
                errors.Add($"运行库不支持所选参数 / Runtime option unsupported: --{argument.Key}");
        if (settings.Types.Contains("ngram-mod", StringComparer.Ordinal))
        {
            int? Read(string key)
            {
                if (settings.Parameters.TryGetValue(key, out var value) && value is not null) return value;
                var prefix = "option-default:" + key + ":";
                var marker = capabilities?.FirstOrDefault(c => c.StartsWith(prefix, StringComparison.Ordinal));
                return marker is not null && int.TryParse(marker[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            }
            var min = Read("spec-ngram-mod-n-min");
            var max = Read("spec-ngram-mod-n-max");
            var explicitLimit = settings.Parameters.Any(p => p.Key is "spec-ngram-mod-n-min" or "spec-ngram-mod-n-max" && p.Value is not null);
            if (explicitLimit && (min is null || max is null))
                errors.Add("无法确认运行库的默认草稿长度，请同时填写最小值和最大值 / Runtime draft defaults are unknown; set both minimum and maximum.");
            if (min is not null && max is not null && min > max)
                errors.Add("ngram-mod 最小草稿长度大于最大值（含运行库默认值） / Minimum exceeds maximum, including runtime defaults.");
        }
        return errors;
    }
}
