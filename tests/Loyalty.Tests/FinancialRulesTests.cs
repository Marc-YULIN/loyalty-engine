using System.Globalization;
using Loyalty.Core;

namespace Loyalty.Tests;

public sealed class FinancialRulesTests
{
    [Theory]
    [InlineData("Gold", "electronics", "100", "1.50")]
    [InlineData("Gold", "apparel", "100", "1.00")]
    [InlineData("Gold", "apparel", "0.5", "0.00")]
    [InlineData("Gold", "apparel", "1.5", "0.02")]
    [InlineData("gold", "ELECTRONICS", "100", "1.50")]
    public void Accrue_CategoryAndMidpoints_ReturnsExpectedAmount(string tier, string category, string amount, string expected)
    {
        var calculator = new CashbackCalculator(new LoyaltyOptions());

        Assert.Equal(Parse(expected), calculator.Accrue(tier, category, Parse(amount)));
    }

    [Theory]
    [InlineData("10", "25", "100", "2.50")]
    [InlineData("0.01", "1", "3", "0.00")]
    [InlineData("0.01", "2", "3", "0.01")]
    [InlineData("10", "100", "100", "10")]
    public void Reverse_ProportionalRefund_ReturnsRoundedAmount(string accrued, string refund, string purchase, string expected)
    {
        var calculator = new CashbackCalculator(new LoyaltyOptions());

        Assert.Equal(Parse(expected), calculator.Reverse(Parse(accrued), Parse(refund), Parse(purchase)));
    }

    [Fact]
    public void ApplyReversal_PreviouslyClaimedFunds_PreservesFullReversalAndClampsClaimable()
    {
        var result = new CashbackCalculator(new LoyaltyOptions()).ApplyReversal(2m, 5m);

        Assert.Equal(5m, result.ReversedAmount);
        Assert.Equal(0m, result.ClaimableAfter);
        Assert.Equal(3m, result.UnfundedAmount);
    }

    [Theory]
    [InlineData(100, 0, 20, 20, 80)]
    [InlineData(10, 0, 20, 10, 0)]
    [InlineData(100, 15, 20, 5, 95)]
    [InlineData(100, 20, 20, 0, 100)]
    [InlineData(100, 25, 20, 0, 100)]
    [InlineData(0, 0, 20, 0, 0)]
    public void Compute_ClaimableAndDailyLimits_ReturnsBoundedClaim(int claimable, int claimed, int cap, int expected, int remaining)
    {
        var result = ClaimPolicy.Compute(claimable, claimed, cap);

        Assert.Equal(expected, result.ClaimNow);
        Assert.Equal(remaining, result.Remaining);
    }

    [Theory]
    [InlineData(-1, 0, 20)]
    [InlineData(10, -1, 20)]
    [InlineData(10, 0, -1)]
    public void Compute_NegativeClaimInput_RejectsInput(int claimable, int claimed, int cap) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ClaimPolicy.Compute(claimable, claimed, cap));

    [Theory]
    [InlineData(DiscountType.FixedAmount, "20", "10", null, "10")]
    [InlineData(DiscountType.Percentage, "0.15", "200", "12", "12")]
    [InlineData(DiscountType.Percentage, "0.10", "25", null, "2.50")]
    public void ComputeDiscount_DiscountTypesAndCaps_ReturnsBoundedDiscount(DiscountType type, string value,
        string amount, string? maximum, string expected)
    {
        var template = new CouponTemplate { DiscountType = type, Value = Parse(value), MaxDiscount = maximum is null ? null : Parse(maximum) };

        Assert.Equal(Parse(expected), CouponRules.ComputeDiscount(template, Parse(amount)));
    }

    [Theory]
    [InlineData(CouponStatus.Available, 1, 50, null)]
    [InlineData(CouponStatus.Available, 0, 50, "expired")]
    [InlineData(CouponStatus.Expired, 1, 50, "expired")]
    [InlineData(CouponStatus.Redeemed, 1, 50, "already_redeemed")]
    [InlineData(CouponStatus.Available, 1, 49, "min_spend_not_met")]
    public void CanRedeem_StatusExpiryAndSpend_ReturnsReason(CouponStatus status, int daysUntilExpiry, int amount, string? expected)
    {
        var now = new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        var coupon = new MemberCoupon { Status = status, ExpiresAt = now.AddDays(daysUntilExpiry),
            Template = new CouponTemplate { MinSpend = 50m, DiscountType = DiscountType.FixedAmount, Value = 5m } };

        Assert.Equal(expected, CouponRules.CanRedeem(coupon, amount, now));
    }

    private static decimal Parse(string value) => decimal.Parse(value, CultureInfo.InvariantCulture);
}
