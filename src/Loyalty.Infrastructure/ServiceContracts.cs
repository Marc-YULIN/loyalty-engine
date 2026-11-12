namespace Loyalty.Infrastructure;

public sealed record IngestResult(bool Duplicate, string EventId, decimal CashbackAmount, string TierCode);
public sealed record ClaimResult(decimal ClaimedAmount, decimal RemainingClaimable, decimal Balance);
public sealed record ClaimResponse(ClaimResult Result, bool Replay, string ResultJson);
public sealed record TierReviewSummary(int Upgraded, int Warned, int Downgraded, int WarningsCleared);
public sealed record CouponTarget(IReadOnlyList<Guid>? MemberIds = null, IReadOnlyList<string>? Tiers = null, Guid? BatchId = null);
public sealed record CouponBatchResult(Guid BatchId, int IssuedCount, int SkippedCount);
public sealed record CouponRedemptionResult(Guid CouponId, decimal Discount, DateTimeOffset RedeemedAt);
