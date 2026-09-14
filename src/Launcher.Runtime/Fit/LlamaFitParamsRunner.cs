using System.Globalization;
using System.Text.RegularExpressions;
using Launcher.Runtime.Processes;

namespace Launcher.Runtime.Fit;

public sealed partial class LlamaFitParamsRunner(IProcessRunner processRunner)
{
    public async Task<LlamaFitRecommendation> RecommendAsync(
        string runtimeRoot,
        string modelPath,
        int minimumContextSize = 4096,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (minimumContextSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumContextSize));
        }

        var root = Path.GetFullPath(runtimeRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var executable = Path.GetFullPath(Path.Combine(root, "llama-fit-params.exe"));
        var fullModelPath = Path.GetFullPath(modelPath);
        if (!executable.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
        {
            throw new FileNotFoundException("当前 llama.cpp Runtime 不包含 llama-fit-params.exe。", executable);
        }

        if (!fullModelPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullModelPath))
        {
            throw new FileNotFoundException("模型不在当前 llama.cpp Runtime 内或已不存在。", fullModelPath);
        }

        var result = await processRunner.RunAsync(
            executable,
            ["--model", fullModelPath, "--fit-ctx", minimumContextSize.ToString(CultureInfo.InvariantCulture)],
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new TimeoutException("llama-fit-params 在两分钟内没有完成。");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"llama-fit-params 退出码为 {result.ExitCode}：{CompactError(result.StandardError)}");
        }

        if (!TryParse(result.StandardOutput, out var recommendation))
        {
            throw new InvalidDataException("llama-fit-params 未返回可识别的 -c/-ngl 参数。");
        }

        return recommendation;
    }

    public static bool TryParse(string output, out LlamaFitRecommendation recommendation)
    {
        recommendation = null!;
        if (string.IsNullOrWhiteSpace(output) || output.Length > 64 * 1024)
        {
            return false;
        }

        var match = FitLineRegex().Matches(output).Cast<Match>().LastOrDefault();
        if (match is null
            || !int.TryParse(match.Groups["ctx"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ctx)
            || !int.TryParse(match.Groups["ngl"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ngl)
            || ctx < 1
            || ngl < 0)
        {
            return false;
        }

        var suffix = match.Groups["suffix"].Value;
        var split = ReadOptionalValue(TensorSplitRegex(), suffix);
        var overrides = ReadOptionalValue(TensorOverrideRegex(), suffix);
        if (!IsSafeNativeValue(split, 512) || !IsSafeNativeValue(overrides, 4096))
        {
            return false;
        }

        recommendation = new LlamaFitRecommendation(ctx, ngl, split, overrides, match.Value.Trim());
        return true;
    }

    private static string? ReadOptionalValue(Regex regex, string value)
    {
        var match = regex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["quoted"].Success
            ? match.Groups["quoted"].Value
            : match.Groups["plain"].Value;
    }

    private static bool IsSafeNativeValue(string? value, int maximumLength) =>
        value is null
        || value.Length <= maximumLength
        && value.IndexOfAny(['\0', '\r', '\n', '"']) < 0;

    private static string CompactError(string value)
    {
        var line = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "没有错误详情" : line;
    }

    [GeneratedRegex(@"(?m)^\s*-c\s+(?<ctx>\d+)\s+-ngl\s+(?<ngl>\d+)(?<suffix>[^\r\n]*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FitLineRegex();

    [GeneratedRegex("(?:^|\\s)-ts\\s+(?:\\\"(?<quoted>[^\\\"]*)\\\"|(?<plain>\\S+))", RegexOptions.CultureInvariant)]
    private static partial Regex TensorSplitRegex();

    [GeneratedRegex("(?:^|\\s)-ot\\s+(?:\\\"(?<quoted>[^\\\"]*)\\\"|(?<plain>\\S+))", RegexOptions.CultureInvariant)]
    private static partial Regex TensorOverrideRegex();
}
