namespace Loyalty.Core;

public enum ActivityEventType { Purchase, Refund }
public enum TierChangeReason { Upgrade, DowngradeWarning, Downgrade, WarningCleared }
public enum LedgerEntryType { CashbackAccrued, CashbackReversed, CashbackClaimed, CouponRedeemed }
public enum CampaignMetric { Spend, PurchaseCount }
public enum DiscountType { FixedAmount, Percentage }
public enum CouponStatus { Available, Redeemed, Expired }

public sealed class Member
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string TierCode { get; set; } = "Bronze";
    public DateTimeOffset TierSince { get; set; }
    public DateTimeOffset? DowngradeWarningAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ActivityEvent
{
    public string EventId { get; set; } = "";
    public Guid MemberId { get; set; }
    public ActivityEventType Type { get; set; }
    public string Category { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? OriginalEventId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class TierChange
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public TierChangeReason Reason { get; set; }
    public decimal RollingSpend { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class Wallet
{
    public Guid MemberId { get; set; }
    public decimal ClaimableCashback { get; set; }
    public decimal Balance { get; set; }
    public long Version { get; set; }
}

public sealed class LedgerEntry
{
    public Guid Id { get; set; }
    public Guid MemberId { get; set; }
    public LedgerEntryType Type { get; set; }
    public decimal Amount { get; set; }
    public decimal ClaimableAfter { get; set; }
    public decimal BalanceAfter { get; set; }
    public string RefId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CashbackClaim
{
    public string IdempotencyKey { get; set; } = "";
    public Guid MemberId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public decimal ClaimedAmount { get; set; }
    public decimal RemainingClaimable { get; set; }
    public string ResultJson { get; set; } = "";
}

public sealed class Campaign
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public CampaignMetric Metric { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string RewardsJson { get; set; } = "[]";
}

public sealed class CampaignScore
{
    public Guid CampaignId { get; set; }
    public Guid MemberId { get; set; }
    public decimal Score { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CouponTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DiscountType DiscountType { get; set; }
    public decimal Value { get; set; }
    public decimal MinSpend { get; set; }
    public decimal? MaxDiscount { get; set; }
    public int ValidDays { get; set; }
}

public sealed class CouponBatch
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public string TargetJson { get; set; } = "{}";
    public int IssuedCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class MemberCoupon
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public Guid MemberId { get; set; }
    public Guid TemplateId { get; set; }
    public CouponTemplate Template { get; set; } = null!;
    public CouponStatus Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public string? RedeemedOrderRef { get; set; }
}
