namespace Loyalty.Core;

public static class CouponRules
{
    public static decimal ComputeDiscount(CouponTemplate template, decimal orderAmount)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentOutOfRangeException.ThrowIfNegative(orderAmount);
        if (orderAmount < template.MinSpend) return 0m;
        var discount = template.DiscountType switch
        {
            DiscountType.FixedAmount => template.Value,
            DiscountType.Percentage => orderAmount * template.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(template), "The discount type is not supported.")
        };
        if (template.MaxDiscount is { } maximum) discount = Math.Min(discount, maximum);
        return decimal.Round(Math.Clamp(discount, 0m, orderAmount), 2, MidpointRounding.ToEven);
    }

    public static string? CanRedeem(MemberCoupon coupon, decimal orderAmount, DateTimeOffset now) =>
        CanRedeem(coupon, coupon.Template, orderAmount, now);

    public static string? CanRedeem(MemberCoupon coupon, CouponTemplate template, decimal orderAmount, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(coupon);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentOutOfRangeException.ThrowIfNegative(orderAmount);
        if (coupon.Status == CouponStatus.Redeemed) return "already_redeemed";
        if (coupon.Status == CouponStatus.Expired || coupon.ExpiresAt <= now) return "expired";
        if (orderAmount < template.MinSpend) return "min_spend_not_met";
        return null;
    }
}
