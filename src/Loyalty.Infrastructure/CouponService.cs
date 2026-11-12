using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Loyalty.Core;
using Microsoft.EntityFrameworkCore;

namespace Loyalty.Infrastructure;

public sealed class CouponService(TransactionRunner transactions, TimeProvider time)
{
    /// <summary>Omitted batch identities are derived from normalized request content for safe resends.</summary>
    public Task<CouponBatchResult> IssueBatchAsync(Guid templateId, CouponTarget target, DateTimeOffset? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        var ids = target.MemberIds?.Distinct().Order().ToArray() ?? [];
        var tiers = target.Tiers?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => x.ToUpperInvariant()).Order(StringComparer.Ordinal).ToArray() ?? [];
        if ((ids.Length == 0) == (tiers.Length == 0))
            throw new DomainException("invalid_coupon_target", "Specify either member IDs or tier codes.");
        if (ids.Contains(Guid.Empty) || target.BatchId == Guid.Empty)
            throw new DomainException("invalid_coupon_target", "Coupon target identities cannot be empty.");
        var fingerprint = JsonSerializer.Serialize(new { templateId, memberIds = ids, tiers, expiresAt = expiresAt?.ToUniversalTime() });
        var batchId = target.BatchId ?? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)).AsSpan(0, 16));
        return transactions.RunAsync(async (db, ct) =>
        {
            var existing = await db.CouponBatches.SingleOrDefaultAsync(x => x.Id == batchId, ct);
            if (existing is not null)
            {
                if (existing.TemplateId != templateId || existing.TargetJson != fingerprint)
                    throw new DomainException("batch_identity_conflict", "The batch identity already belongs to a different request.", 409);
                return new CouponBatchResult(batchId, 0, existing.IssuedCount);
            }
            var template = await db.CouponTemplates.SingleOrDefaultAsync(x => x.Id == templateId, ct)
                ?? throw new DomainException("coupon_template_not_found", "Coupon template was not found.", 404);
            var now = time.GetUtcNow();
            var expiration = expiresAt ?? now.AddDays(template.ValidDays);
            if (expiration <= now) throw new DomainException("invalid_expiration", "Coupon expiration must be in the future.");
            var members = ids.Length > 0
                ? await db.Members.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct)
                : (await db.Members.AsNoTracking().Select(x => new { x.Id, x.TierCode }).ToListAsync(ct))
                    .Where(x => tiers.Contains(x.TierCode, StringComparer.OrdinalIgnoreCase)).Select(x => x.Id).ToList();
            var batch = new CouponBatch { Id = batchId, TemplateId = templateId, TargetJson = fingerprint, CreatedAt = now, IssuedCount = members.Count };
            db.CouponBatches.Add(batch);
            await db.SaveChangesAsync(ct);
            foreach (var chunk in members.Chunk(1000))
            {
                db.MemberCoupons.AddRange(chunk.Select(memberId => new MemberCoupon
                {
                    Id = Guid.NewGuid(), BatchId = batchId, MemberId = memberId, TemplateId = templateId,
                    Status = CouponStatus.Available, ExpiresAt = expiration
                }));
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
            return new CouponBatchResult(batchId, members.Count, ids.Length > 0 ? ids.Length - members.Count : 0);
        }, cancellationToken);
    }

    public Task<CouponRedemptionResult> RedeemAsync(Guid memberId, Guid couponId, decimal orderAmount, string orderRef,
        CancellationToken cancellationToken = default)
    {
        MoneyStorage.Validate(orderAmount);
        if (orderAmount < 0m || string.IsNullOrWhiteSpace(orderRef) || orderRef.Length > 200)
            throw new DomainException("invalid_order", "A nonnegative order amount and order reference are required.");
        return transactions.RunAsync(async (db, ct) =>
        {
            var now = time.GetUtcNow();
            var coupon = await db.MemberCoupons.AsNoTracking().Include(x => x.Template)
                .SingleOrDefaultAsync(x => x.Id == couponId && x.MemberId == memberId, ct)
                ?? throw new DomainException("coupon_not_found", "Member coupon was not found.", 404);
            var reason = CouponRules.CanRedeem(coupon, orderAmount, now);
            if (reason is not null) throw Failure(reason);
            var affected = await db.MemberCoupons.Where(x => x.Id == couponId && x.MemberId == memberId
                && x.Status == CouponStatus.Available && x.ExpiresAt > now)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, CouponStatus.Redeemed)
                    .SetProperty(x => x.RedeemedAt, (DateTimeOffset?)now).SetProperty(x => x.RedeemedOrderRef, orderRef), ct);
            if (affected == 0)
            {
                var current = await db.MemberCoupons.AsNoTracking().SingleAsync(x => x.Id == couponId, ct);
                throw Failure(CouponRules.CanRedeem(current, coupon.Template, orderAmount, now) ?? "already_redeemed");
            }
            var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.MemberId == memberId, ct);
            var discount = CouponRules.ComputeDiscount(coupon.Template, orderAmount);
            db.LedgerEntries.Add(new LedgerEntry
            {
                Id = Guid.NewGuid(), MemberId = memberId, Type = LedgerEntryType.CouponRedeemed, Amount = discount,
                ClaimableAfter = wallet?.ClaimableCashback ?? 0m, BalanceAfter = wallet?.Balance ?? 0m,
                RefId = couponId.ToString(), CreatedAt = now
            });
            return new CouponRedemptionResult(couponId, discount, now);
        }, cancellationToken);
    }

    public Task<int> ExpireAsync(CancellationToken cancellationToken = default) => transactions.RunAsync(async (db, ct) =>
    {
        var now = time.GetUtcNow();
        return await db.MemberCoupons.Where(x => x.Status == CouponStatus.Available && x.ExpiresAt <= now)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, CouponStatus.Expired), ct);
    }, cancellationToken);

    private static DomainException Failure(string reason) => new(reason, "The coupon cannot be redeemed.", reason == "min_spend_not_met" ? 422 : 409);
}
