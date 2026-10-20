using System.Globalization;
using System.Text.RegularExpressions;
using Launcher.Core.Configuration;

namespace Launcher.Runtime.Monitoring;

public static partial class DraftAcceptanceParser
{
    public static bool TryParse(string line, out DraftAcceptanceSnapshot? value)
    {
        value = null;
        if (!line.Contains("draft acceptance", StringComparison.Ordinal)) return false;
        // Only slot timing summaries describe a completed request. Global speculative
        // statistics can be cumulative and must not replace this request's result.
        var match = TimingLine().Match(line);
        if (!match.Success
            || !double.TryParse(match.Groups["rate"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate)
            || !long.TryParse(match.Groups["accepted"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var accepted)
            || !long.TryParse(match.Groups["draft"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var drafted)) return false;
        var parsed = new DraftAcceptanceSnapshot(rate, accepted, drafted, DateTimeOffset.UtcNow);
        if (!parsed.IsValid) return false;
        value = parsed;
        return true;
    }

    [GeneratedRegex(@"\bslot\s+print_timings?:\s+id\s+\d+\s*\|\s*task\s+\d+\s*\|\s*draft acceptance\s*=\s*(?<rate>\d+(?:\.\d+)?)\s*\(\s*(?<accepted>\d+)\s+accepted\s*/\s*(?<draft>\d+)\s+generated\s*\)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex TimingLine();
}
