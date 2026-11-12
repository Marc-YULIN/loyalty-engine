using Loyalty.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loyalty.Infrastructure;

public sealed record SeedResult(IReadOnlyList<Guid> MemberIds, IReadOnlyList<Guid> TemplateIds, Guid CampaignId);

public sealed class DemoDataSeeder(TransactionRunner transactions, EventIngestionService ingestion,
    IOptions<LoyaltyOptions> options, TimeProvider time)
{
    public async Task<SeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        Guid[] memberIds = [Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"), Guid.Parse("44444444-4444-4444-4444-444444444444"), Guid.Parse("55555555-5555-5555-5555-555555555555")];
        Guid[] templateIds = [Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1"), Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2")];
        var campaignId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var now = time.GetUtcNow();
        await transactions.RunAsync(async (db, ct) =>
        {
            var baseTier = options.Value.Tiers.MinBy(x => x.Value).Key;
            for (var i = 0; i < memberIds.Length; i++)
            {
                if (await db.Members.AnyAsync(x => x.Id == memberIds[i], ct)) continue;
                db.Members.Add(new Member { Id = memberIds[i], ExternalId = $"demo-member-{i + 1}", DisplayName = $"Sample Member {i + 1}",
                    TierCode = baseTier, TierSince = now.AddDays(-60), CreatedAt = now.AddDays(-60) });
                db.Wallets.Add(new Wallet { MemberId = memberIds[i] });
            }
            if (!await db.CouponTemplates.AnyAsync(x => x.Id == templateIds[0], ct))
                db.CouponTemplates.Add(new CouponTemplate { Id = templateIds[0], Name = "Welcome Credit", DiscountType = DiscountType.FixedAmount,
                    Value = 10m, MinSpend = 50m, ValidDays = 30 });
            if (!await db.CouponTemplates.AnyAsync(x => x.Id == templateIds[1], ct))
                db.CouponTemplates.Add(new CouponTemplate { Id = templateIds[1], Name = "Member Appreciation", DiscountType = DiscountType.Percentage,
                    Value = 0.1m, MinSpend = 100m, MaxDiscount = 30m, ValidDays = 14 });
            if (!await db.Campaigns.AnyAsync(x => x.Id == campaignId, ct))
                db.Campaigns.Add(new Campaign { Id = campaignId, Name = "Autumn Member Challenge", Metric = CampaignMetric.Spend,
                    StartsAt = now.AddDays(-60), EndsAt = now.AddDays(30), RewardsJson = "[{\"from\":1,\"to\":3,\"reward\":\"Member gift\"}]" });
            return true;
        }, cancellationToken);
        for (var member = 0; member < memberIds.Length; member++)
        {
            for (var sample = 0; sample < 6; sample++)
                await ingestion.IngestAsync(new ActivityEventDto($"demo-purchase-{member + 1}-{sample + 1}", memberIds[member], ActivityEventType.Purchase,
                    sample % 2 == 0 ? "electronics" : "general", (member + 1) * 125m, now.AddDays(-59 + sample * 11)), cancellationToken);
        }
        return new SeedResult(memberIds, templateIds, campaignId);
    }
}
