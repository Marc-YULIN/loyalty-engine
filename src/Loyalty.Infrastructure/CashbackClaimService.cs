using System.Text.Json;
using Loyalty.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loyalty.Infrastructure;

public sealed class CashbackClaimService(TransactionRunner transactions, IOptions<LoyaltyOptions> options, TimeProvider time)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<ClaimResponse> ClaimAsync(Guid memberId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
            throw new DomainException("invalid_idempotency_key", "A nonempty idempotency key of at most 200 characters is required.", 400);
        return transactions.RunAsync(async (db, ct) =>
        {
            var existing = await db.CashbackClaims.SingleOrDefaultAsync(x => x.MemberId == memberId && x.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
                return new ClaimResponse(JsonSerializer.Deserialize<ClaimResult>(existing.ResultJson, JsonOptions)!, true, existing.ResultJson);
            var member = await db.Members.SingleOrDefaultAsync(x => x.Id == memberId, ct)
                ?? throw new DomainException("member_not_found", "Member was not found.", 404);
            var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.MemberId == memberId, ct);
            if (wallet is null)
            {
                wallet = new Wallet { MemberId = memberId };
                db.Wallets.Add(wallet);
            }
            var now = time.GetUtcNow();
            var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            var already = await db.CashbackClaims.Where(x => x.MemberId == memberId && x.RequestedAt >= day && x.RequestedAt < day.AddDays(1))
                .SumAsync(x => (decimal?)x.ClaimedAmount, ct) ?? 0m;
            var cap = options.Value.DailyClaimCap.First(x => string.Equals(x.Key, member.TierCode, StringComparison.OrdinalIgnoreCase)).Value;
            var (claimed, remaining) = ClaimPolicy.Compute(wallet.ClaimableCashback, already, cap);
            MoneyStorage.Validate(wallet.Balance + claimed);
            wallet.ClaimableCashback = remaining;
            wallet.Balance += claimed;
            wallet.Version++;
            var result = new ClaimResult(claimed, remaining, wallet.Balance);
            var json = JsonSerializer.Serialize(result, JsonOptions);
            db.CashbackClaims.Add(new CashbackClaim
            {
                MemberId = memberId, IdempotencyKey = idempotencyKey, RequestedAt = now,
                ClaimedAmount = claimed, RemainingClaimable = remaining, ResultJson = json
            });
            db.LedgerEntries.Add(new LedgerEntry
            {
                Id = Guid.NewGuid(), MemberId = memberId, Type = LedgerEntryType.CashbackClaimed, Amount = claimed,
                ClaimableAfter = remaining, BalanceAfter = wallet.Balance, RefId = idempotencyKey, CreatedAt = now
            });
            return new ClaimResponse(result, false, json);
        }, cancellationToken);
    }
}
