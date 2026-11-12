using Loyalty.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Loyalty.Infrastructure;

public sealed class TierReviewWorker(TierReviewService review, IOptions<LoyaltyOptions> options, TimeProvider time,
    ILogger<TierReviewWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableTierReviewWorker) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var next = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddMinutes(10);
            if (next <= now) next = next.AddDays(1);
            try
            {
                await Task.Delay(next - now, time, stoppingToken);
                var result = await review.RunAsync(time.GetUtcNow(), stoppingToken);
                if (logger.IsEnabled(LogLevel.Information))
                    InfrastructureLog.TierReviewCompleted(logger, result.Upgraded, result.Warned, result.Downgraded, result.WarningsCleared, null);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { InfrastructureLog.TierReviewFailed(logger, exception); }
        }
    }
}

public sealed class CouponExpiryWorker(CouponService coupons, TimeProvider time, ILogger<CouponExpiryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
                var expired = await coupons.ExpireAsync(stoppingToken);
                if (expired > 0) InfrastructureLog.CouponsExpired(logger, expired, null);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { InfrastructureLog.CouponExpiryFailed(logger, exception); }
        }
    }
}
