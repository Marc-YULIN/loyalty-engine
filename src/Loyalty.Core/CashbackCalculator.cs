namespace Loyalty.Core;

public sealed record CashbackReversal(decimal ReversedAmount, decimal ClaimableAfter, decimal UnfundedAmount);

public sealed class CashbackCalculator(LoyaltyOptions options)
{
    public decimal Accrue(string tier, string category, decimal amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        var rates = options.CashbackRates.FirstOrDefault(r => string.Equals(r.Key, tier, StringComparison.OrdinalIgnoreCase)).Value
            ?? throw new ArgumentException("The cashback tier is not configured.", nameof(tier));
        var categoryRate = rates.FirstOrDefault(r => string.Equals(r.Key, category, StringComparison.OrdinalIgnoreCase));
        var rate = categoryRate.Key is not null ? categoryRate.Value : rates["*"];
        // Monetary accruals and proportional reversals use banker's rounding to two decimal places.
        return decimal.Round(amount * rate, 2, MidpointRounding.ToEven);
    }

    public decimal Reverse(decimal originalAccrual, decimal refundAmount, decimal originalAmount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(originalAccrual);
        ArgumentOutOfRangeException.ThrowIfNegative(refundAmount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(originalAmount);
        if (refundAmount > originalAmount)
            throw new ArgumentOutOfRangeException(nameof(refundAmount), "A refund cannot exceed the original purchase amount.");
        return decimal.Round(originalAccrual * refundAmount / originalAmount, 2, MidpointRounding.ToEven);
    }

    public CashbackReversal ApplyReversal(decimal claimable, decimal reversalAmount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(claimable);
        ArgumentOutOfRangeException.ThrowIfNegative(reversalAmount);
        // Keep the full reversal in the ledger even when part of it has already been claimed.
        return new CashbackReversal(reversalAmount, Math.Max(0m, claimable - reversalAmount), Math.Max(0m, reversalAmount - claimable));
    }
}
