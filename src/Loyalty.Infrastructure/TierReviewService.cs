using Loyalty.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loyalty.Infrastructure;

public sealed class TierReviewService(TransactionRunner transactions, IOptions<LoyaltyOptions> options)
{
    public async Task<TierReviewSummary> RunAsync(DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var counts = new int[4];
        for (var page = 0; ; page++)
        {
            var summary = await transactions.RunAsync(async (db, ct) =>
            {
                var members = await db.Members.OrderBy(x => x.Id).Skip(page * 500).Take(500).ToListAsync(ct);
                var result = new int[4];
                foreach (var member in members)
                {
                    if (asOf < member.CreatedAt || asOf < member.TierSince || asOf < member.DowngradeWarningAt) continue;
                    var spend = await SpendQueries.RollingSpendAsync(db, member.Id, asOf, options.Value.RollingWindowDays, ct);
                    var decision = TierPolicy.Evaluate(new TierState(member.TierCode, member.TierSince, member.DowngradeWarningAt), spend, asOf, options.Value);
                    if (decision.Reason is not { } reason) continue;
                    ApplyDecision(db, member, decision, spend, asOf);
                    result[(int)reason]++;
                }
                return (members.Count, result);
            }, cancellationToken);
            for (var i = 0; i < counts.Length; i++) counts[i] += summary.result[i];
            if (summary.Count < 500) break;
        }
        return new TierReviewSummary(counts[0], counts[1], counts[2], counts[3]);
    }

    internal static void ApplyDecision(LoyaltyDbContext db, Member member, TierDecision decision, decimal spend, DateTimeOffset now)
    {
        db.TierChanges.Add(new TierChange
        {
            Id = Guid.NewGuid(), MemberId = member.Id, From = member.TierCode, To = decision.TierCode,
            Reason = decision.Reason!.Value, RollingSpend = spend, At = now
        });
        member.TierCode = decision.TierCode;
        member.TierSince = decision.TierSince;
        member.DowngradeWarningAt = decision.DowngradeWarningAt;
    }
}
