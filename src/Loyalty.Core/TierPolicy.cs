namespace Loyalty.Core;

public sealed record TierState(string TierCode, DateTimeOffset TierSince, DateTimeOffset? DowngradeWarningAt = null);
public sealed record NextTierProgress(string? TierCode, decimal? Threshold, decimal Remaining);
public sealed record TierDecision(
    string TierCode,
    DateTimeOffset TierSince,
    DateTimeOffset? DowngradeWarningAt,
    TierChangeReason? Reason,
    NextTierProgress NextTierProgress)
{
    public bool Changed => Reason is not null;
}

public static class TierPolicy
{
    public static TierDecision Evaluate(TierState state, decimal rollingSpend, DateTimeOffset asOf, LoyaltyOptions o)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(o);
        var tiers = o.Tiers.OrderBy(t => t.Value).ToArray();
        var current = Array.FindIndex(tiers, t => string.Equals(t.Key, state.TierCode, StringComparison.OrdinalIgnoreCase));
        if (current < 0)
            throw new ArgumentException("The current tier is not configured.", nameof(state));

        var qualified = 0;
        for (var i = 0; i < tiers.Length; i++)
            if (rollingSpend >= tiers[i].Value) qualified = i;

        var next = current;
        var since = state.TierSince;
        var warning = state.DowngradeWarningAt;
        TierChangeReason? reason = null;

        if (qualified > current)
        {
            next = qualified;
            since = asOf;
            warning = null;
            reason = TierChangeReason.Upgrade;
        }
        else if (qualified < current)
        {
            if (warning is null)
            {
                warning = asOf;
                reason = TierChangeReason.DowngradeWarning;
            }
            else if (asOf >= warning.Value.AddDays(o.DowngradeGraceDays))
            {
                // Each grace period permits only one tier of downward movement.
                next = current - 1;
                since = asOf;
                warning = qualified < next ? asOf : null;
                reason = TierChangeReason.Downgrade;
            }
        }
        else if (warning is not null)
        {
            warning = null;
            reason = TierChangeReason.WarningCleared;
        }

        var progress = next + 1 < tiers.Length
            ? new NextTierProgress(tiers[next + 1].Key, tiers[next + 1].Value, Math.Max(0m, tiers[next + 1].Value - rollingSpend))
            : new NextTierProgress(null, null, 0m);
        return new TierDecision(tiers[next].Key, since, warning, reason, progress);
    }
}
