namespace Launcher.Models.Profiles;

public static class CodexStreamIdleTimeout
{
    public const int StepMinutes = 5;
    public const int MaximumMinutes = int.MaxValue / 60_000 / StepMinutes * StepMinutes;

    public static bool IsValid(int? minutes) => minutes is null
        || minutes >= StepMinutes && minutes <= MaximumMinutes && minutes % StepMinutes == 0;

    public static int? ToMilliseconds(int? minutes)
    {
        if (!IsValid(minutes))
            throw new ArgumentOutOfRangeException(nameof(minutes), "SSE 空闲等待时间必须为 5 分钟的正整数倍。");
        return minutes is int value ? checked(value * 60_000) : null;
    }
}
