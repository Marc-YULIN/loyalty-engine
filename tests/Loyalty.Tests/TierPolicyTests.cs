using Loyalty.Core;

namespace Loyalty.Tests;

public sealed class TierPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Bronze", 20000, -1, "Platinum", TierChangeReason.Upgrade, false)]
    [InlineData("Gold", 0, -1, "Gold", TierChangeReason.DowngradeWarning, true)]
    [InlineData("Gold", 0, 13, "Gold", null, true)]
    [InlineData("Platinum", 0, 15, "Gold", TierChangeReason.Downgrade, true)]
    [InlineData("Gold", 5000, 5, "Gold", TierChangeReason.WarningCleared, false)]
    [InlineData("Bronze", 0, -1, "Bronze", null, false)]
    [InlineData("Gold", 20000, 5, "Platinum", TierChangeReason.Upgrade, false)]
    [InlineData("Silver", 0, 14, "Bronze", TierChangeReason.Downgrade, false)]
    public void Evaluate_TierAndGraceScenarios_ReturnsExpectedDecision(string current, int spend, int warningAge,
        string expected, TierChangeReason? reason, bool hasWarning)
    {
        var state = new TierState(current, Now.AddDays(-60), warningAge < 0 ? null : Now.AddDays(-warningAge));

        var result = TierPolicy.Evaluate(state, spend, Now, new LoyaltyOptions());

        Assert.Equal(expected, result.TierCode);
        Assert.Equal(reason, result.Reason);
        Assert.Equal(reason is not null, result.Changed);
        Assert.Equal(hasWarning, result.DowngradeWarningAt is not null);
        Assert.Equal(reason is TierChangeReason.Upgrade or TierChangeReason.Downgrade ? Now : state.TierSince, result.TierSince);
        if (reason == TierChangeReason.DowngradeWarning || reason == TierChangeReason.Downgrade && hasWarning)
            Assert.Equal(Now, result.DowngradeWarningAt);
        if (reason is null) Assert.Equal(state.DowngradeWarningAt, result.DowngradeWarningAt);
    }

    [Theory]
    [InlineData("Bronze", 250, "Silver", 1000, 750)]
    [InlineData("Silver", 2000, "Gold", 5000, 3000)]
    [InlineData("Gold", 7500, "Platinum", 20000, 12500)]
    public void Evaluate_BelowNextThreshold_ReturnsProgress(string tier, int spend, string next, int threshold, int remaining)
    {
        var result = TierPolicy.Evaluate(new TierState(tier, Now), spend, Now, new LoyaltyOptions());

        Assert.Equal(next, result.NextTierProgress.TierCode);
        Assert.Equal(threshold, result.NextTierProgress.Threshold);
        Assert.Equal(remaining, result.NextTierProgress.Remaining);
    }

    [Fact]
    public void Evaluate_HighestTier_ReturnsNoNextThreshold()
    {
        var result = TierPolicy.Evaluate(new TierState("Platinum", Now), 20000m, Now, new LoyaltyOptions());

        Assert.Null(result.NextTierProgress.TierCode);
        Assert.Null(result.NextTierProgress.Threshold);
        Assert.Equal(0m, result.NextTierProgress.Remaining);
    }
}
