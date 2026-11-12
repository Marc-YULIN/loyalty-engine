using Loyalty.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Loyalty.Infrastructure;

/// <summary>Serializes local SQLite writers while version tokens protect independent processes.</summary>
public sealed class MutationGate
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed class TransactionRunner(IDbContextFactory<LoyaltyDbContext> factory, MutationGate gate)
{
    public async Task<T> RunAsync<T>(Func<LoyaltyDbContext, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default)
    {
        await gate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                await using var db = await factory.CreateDbContextAsync(cancellationToken);
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                    var result = await action(db, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return result;
                }
                catch (Exception exception) when (attempt < 2 && IsRetryable(exception))
                {
                    db.ChangeTracker.Clear();
                }
                catch (Exception exception) when (IsRetryable(exception))
                {
                    throw new DomainException("concurrent_update", "The operation conflicted with another update. Retry the request.", 409);
                }
            }
        }
        finally { gate.Semaphore.Release(); }
    }

    private static bool IsRetryable(Exception exception) => exception is DbUpdateConcurrencyException
        || exception is SqliteException { SqliteErrorCode: 5 or 6 }
        || exception is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 or 19 } };
}

public static class SpendQueries
{
    /// <summary>Refunds reduce only purchases that remain inside the rolling window.</summary>
    public static async Task<decimal> RollingSpendAsync(LoyaltyDbContext db, Guid memberId, DateTimeOffset asOf,
        int rollingWindowDays, CancellationToken cancellationToken = default)
    {
        var start = asOf.AddDays(-rollingWindowDays);
        var purchases = db.ActivityEvents.Where(x => x.MemberId == memberId && x.Type == ActivityEventType.Purchase
            && x.OccurredAt >= start && x.OccurredAt <= asOf);
        var purchaseTotal = await purchases.SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;
        var refunds = await db.ActivityEvents.Where(x => x.MemberId == memberId && x.Type == ActivityEventType.Refund
            && x.OccurredAt <= asOf && purchases.Select(p => p.EventId).Contains(x.OriginalEventId!))
            .SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;
        return Math.Max(0m, purchaseTotal - refunds);
    }
}
