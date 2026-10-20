using System.Text.Json.Serialization;

namespace Launcher.Core.Configuration;

/// <summary>The most recently observed native per-request speculative decoding result.</summary>
public sealed record DraftAcceptanceSnapshot(
    double Rate,
    long AcceptedTokens,
    long DraftTokens,
    DateTimeOffset ObservedAtUtc)
{
    public bool IncludesNgram { get; init; }

    [JsonIgnore]
    public bool IsValid => double.IsFinite(Rate) && Rate is >= 0 and <= 1
        && DraftTokens > 0 && AcceptedTokens >= 0 && AcceptedTokens <= DraftTokens
        && Math.Abs(Rate - (double)AcceptedTokens / DraftTokens) <= 0.00001;
}
