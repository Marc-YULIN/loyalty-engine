using Loyalty.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loyalty.Infrastructure;

public sealed class EventIngestionService(TransactionRunner transactions, IOptions<LoyaltyOptions> options,
    TimeProvider time, ILeaderboardStore leaderboard)
{
    public async Task<IngestResult> IngestAsync(ActivityEventDto request, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        Validate(request, now);
        var changes = new List<(Guid CampaignId, Guid MemberId, decimal Delta)>();
        var result = await transactions.RunAsync(async (db, ct) =>
        {
            changes.Clear();
            var member = await db.Members.SingleOrDefaultAsync(x => x.Id == request.MemberId, ct)
                ?? throw new DomainException("member_not_found", "Member was not found.", 404);
            if (await db.ActivityEvents.AnyAsync(x => x.EventId == request.EventId, ct))
                return new IngestResult(true, request.EventId, 0m, member.TierCode);

            var wallet = await db.Wallets.SingleOrDefaultAsync(x => x.MemberId == member.Id, ct);
            if (wallet is null)
            {
                wallet = new Wallet { MemberId = member.Id };
                db.Wallets.Add(wallet);
            }
            ActivityEvent? original = null;
            decimal refunded = 0m;
            if (request.Type == ActivityEventType.Refund)
            {
                original = await db.ActivityEvents.SingleOrDefaultAsync(x => x.EventId == request.OriginalEventId, ct);
                if (original is null || original.Type != ActivityEventType.Purchase || original.MemberId != member.Id
                    || original.Category != request.Category || request.OccurredAt < original.OccurredAt)
                    throw new DomainException("invalid_original_event", "Refund must reference an earlier purchase for the same member and category.");
                refunded = await db.ActivityEvents.Where(x => x.OriginalEventId == original.EventId && x.Type == ActivityEventType.Refund)
                    .SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
                if (request.Amount > original.Amount - refunded)
                    throw new DomainException("refund_exceeds_purchase", "Cumulative refunds cannot exceed the original purchase amount.");
            }

            db.ActivityEvents.Add(new ActivityEvent
            {
                EventId = request.EventId, MemberId = member.Id, Type = request.Type, Category = request.Category,
                Amount = request.Amount, OccurredAt = request.OccurredAt, OriginalEventId = request.OriginalEventId, ReceivedAt = now
            });
            // Persist the event inside the transaction before evaluating SQL aggregates.
            await db.SaveChangesAsync(ct);
            var calculator = new CashbackCalculator(options.Value);
            decimal cashback;
            if (original is null)
            {
                var spend = await SpendQueries.RollingSpendAsync(db, member.Id, now, options.Value.RollingWindowDays, ct);
                var decision = TierPolicy.Evaluate(new TierState(member.TierCode, member.TierSince, member.DowngradeWarningAt), spend, now, options.Value);
                // Purchases can upgrade or clear warnings; downgrades belong to the review job.
                if (decision.Reason is TierChangeReason.Upgrade or TierChangeReason.WarningCleared)
                    TierReviewService.ApplyDecision(db, member, decision, spend, now);
                cashback = calculator.Accrue(member.TierCode, request.Category, request.Amount);
                MoneyStorage.Validate(wallet.ClaimableCashback + cashback);
                wallet.ClaimableCashback += cashback;
            }
            else
            {
                var accrued = await db.LedgerEntries.Where(x => x.RefId == original.EventId && x.MemberId == member.Id
                    && x.Type == LedgerEntryType.CashbackAccrued).SumAsync(x => (decimal?)x.Amount, ct) ?? 0m;
                var previousReversal = calculator.Reverse(accrued, refunded, original.Amount);
                var cumulativeReversal = calculator.Reverse(accrued, refunded + request.Amount, original.Amount);
                cashback = -(cumulativeReversal - previousReversal);
                wallet.ClaimableCashback = calculator.ApplyReversal(wallet.ClaimableCashback, -cashback).ClaimableAfter;
            }
            wallet.Version++;
            db.LedgerEntries.Add(new LedgerEntry
            {
                Id = Guid.NewGuid(), MemberId = member.Id, Type = original is null ? LedgerEntryType.CashbackAccrued : LedgerEntryType.CashbackReversed,
                Amount = cashback, ClaimableAfter = wallet.ClaimableCashback, BalanceAfter = wallet.Balance, RefId = request.EventId, CreatedAt = now
            });

            var purchaseTime = original?.OccurredAt ?? request.OccurredAt;
            // Campaigns must exist before their purchases; backdated campaign creation has no per-event attribution in this schema.
            var campaigns = await db.Campaigns.Where(x => x.StartsAt <= purchaseTime && x.EndsAt > purchaseTime
                && (original != null || (x.StartsAt <= now && x.EndsAt > now))).ToListAsync(ct);
            foreach (var campaign in campaigns)
            {
                var score = await db.CampaignScores.SingleOrDefaultAsync(x => x.CampaignId == campaign.Id && x.MemberId == member.Id, ct);
                if (original is not null && score is null) continue;
                var delta = campaign.Metric == CampaignMetric.Spend ? request.Amount : original is null || refunded + request.Amount == original.Amount ? 1m : 0m;
                if (original is not null) delta = -delta;
                if (score is null)
                {
                    score = new CampaignScore { CampaignId = campaign.Id, MemberId = member.Id };
                    db.CampaignScores.Add(score);
                }
                MoneyStorage.Validate(score.Score + delta);
                score.Score = Math.Max(0m, score.Score + delta);
                score.UpdatedAt = now;
                changes.Add((campaign.Id, member.Id, delta));
            }
            return new IngestResult(false, request.EventId, cashback, member.TierCode);
        }, cancellationToken);

        foreach (var change in changes)
            await leaderboard.IncrementAsync(change.CampaignId, change.MemberId, change.Delta, cancellationToken);
        return result;
    }

    private static void Validate(ActivityEventDto request, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(request.EventId) || request.EventId.Length > 200
            || string.IsNullOrWhiteSpace(request.Category) || request.Category.Length > 100 || request.MemberId == Guid.Empty
            || !Enum.IsDefined(request.Type))
            throw new DomainException("invalid_event", "Event identity, member, category, and type are required.");
        MoneyStorage.Validate(request.Amount);
        if (request.Amount <= 0m) throw new DomainException("invalid_amount", "Event amount must be positive.");
        if (request.OccurredAt > now) throw new DomainException("future_event", "Event occurrence cannot be in the future.");
        if (request.Type == ActivityEventType.Refund && string.IsNullOrWhiteSpace(request.OriginalEventId))
            throw new DomainException("original_event_required", "A refund requires the original purchase event.");
        if (request.Type == ActivityEventType.Purchase && request.OriginalEventId is not null)
            throw new DomainException("invalid_original_event", "A purchase cannot reference another event.");
    }
}
