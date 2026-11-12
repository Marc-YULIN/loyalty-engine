using Microsoft.Extensions.Logging;

namespace Loyalty.Infrastructure;

internal static class InfrastructureLog
{
    internal static readonly Action<ILogger, Exception?> CacheUnavailable = LoggerMessage.Define(
        LogLevel.Warning, new EventId(1, "CacheUnavailable"), "Leaderboard cache unavailable; using database for 30 seconds");
    internal static readonly Action<ILogger, int, int, int, int, Exception?> TierReviewCompleted = LoggerMessage.Define<int, int, int, int>(
        LogLevel.Information, new EventId(2, "TierReviewCompleted"),
        "Tier review completed: {Upgraded} upgraded, {Warned} warned, {Downgraded} downgraded, {Cleared} cleared");
    internal static readonly Action<ILogger, Exception?> TierReviewFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(3, "TierReviewFailed"), "Scheduled tier review failed");
    internal static readonly Action<ILogger, int, Exception?> CouponsExpired = LoggerMessage.Define<int>(
        LogLevel.Information, new EventId(4, "CouponsExpired"), "Expired {CouponCount} coupons");
    internal static readonly Action<ILogger, Exception?> CouponExpiryFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(5, "CouponExpiryFailed"), "Coupon expiration failed");
}
