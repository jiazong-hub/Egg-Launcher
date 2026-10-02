namespace Launcher.Core.Configuration;

/// <summary>Shared configuration bounds; the ten-percent allowance is an empirical safeguard.</summary>
public static class LongConversationLimits
{
    public const int MinimumTokens = 1_024;

    public static int MinimumReserve(int context)
        => Math.Max(MinimumTokens, context / 11 + 1);

    public static int MaximumReserve(int context) => context - MinimumTokens;

    public static IReadOnlyList<string> Validate(int context, int reserve, int? toolLimit)
    {
        var errors = new List<string>();
        if (reserve < MinimumTokens)
            errors.Add("压缩安全余量不能小于 1,024 tokens。");
        if (context > 0)
        {
            var minimum = MinimumReserve(context);
            var maximum = MaximumReserve(context);
            if (minimum > maximum)
                errors.Add("当前上下文过小，无法同时满足摘要预留和自动压缩线至少 1,024 tokens 的要求。");
            else if (reserve < minimum || reserve > maximum)
                errors.Add($"压缩安全余量必须在 {minimum:N0}～{maximum:N0} tokens 之间：需满足 Context < 11 × 余量，且自动压缩线至少 1,024 tokens。");
        }
        if (toolLimit is int tool && (tool < MinimumTokens || tool >= reserve))
            errors.Add("工具结果历史上限必须至少为 1,024 tokens，且严格小于压缩安全余量。");
        return errors;
    }
}
