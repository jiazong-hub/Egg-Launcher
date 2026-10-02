using Launcher.Core.Configuration;

namespace Launcher.Tests;

public sealed class LongConversationLimitsTests
{
    [Theory]
    [InlineData(32768)]
    [InlineData(98304)]
    [InlineData(262144)]
    public void ReserveBoundary_EnforcesStrictTenPercentAllowance(int context)
    {
        var minimum = LongConversationLimits.MinimumReserve(context);
        Assert.True(context < 11L * minimum);
        Assert.NotEmpty(LongConversationLimits.Validate(context, minimum - 1, null));
        Assert.Empty(LongConversationLimits.Validate(context, minimum, null));
        Assert.Empty(LongConversationLimits.Validate(context, context - 1024, null));
        Assert.NotEmpty(LongConversationLimits.Validate(context, context - 1023, null));
    }

    [Theory]
    [InlineData(1023, false)]
    [InlineData(1024, true)]
    [InlineData(8191, true)]
    [InlineData(8192, false)]
    public void ToolBudget_MustBeBelowReserve(int limit, bool valid)
        => Assert.Equal(valid, LongConversationLimits.Validate(32768, 8192, limit).Count == 0);

    [Fact]
    public void TinyContext_CannotMeetMinimums()
        => Assert.NotEmpty(LongConversationLimits.Validate(1024, 1024, null));
}
