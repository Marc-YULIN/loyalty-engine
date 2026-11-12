namespace Loyalty.Core;

public static class ClaimPolicy
{
    public static (decimal ClaimNow, decimal Remaining) Compute(decimal claimable, decimal alreadyClaimedToday, decimal dailyCap)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(claimable);
        ArgumentOutOfRangeException.ThrowIfNegative(alreadyClaimedToday);
        ArgumentOutOfRangeException.ThrowIfNegative(dailyCap);
        var claimNow = Math.Min(claimable, Math.Max(0m, dailyCap - alreadyClaimedToday));
        return (claimNow, claimable - claimNow);
    }
}
