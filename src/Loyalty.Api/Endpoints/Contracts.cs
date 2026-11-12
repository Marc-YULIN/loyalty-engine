using System.ComponentModel.DataAnnotations;
using Loyalty.Core;
using Loyalty.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Loyalty.Api.Endpoints;

public sealed class SecurityOptions
{
    [Required, MinLength(8)]
    public string ApiKey { get; set; } = "";
}

public sealed record CreateMemberRequest(string ExternalId, string DisplayName);
public sealed record CreateCampaignRequest(string Name, CampaignMetric Metric, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string RewardsJson = "[]");
public sealed record CreateCouponTemplateRequest(string Name, DiscountType DiscountType, decimal Value,
    decimal MinSpend, decimal? MaxDiscount, int ValidDays);
public sealed record IssueCouponsRequest(Guid TemplateId, CouponTarget Target, DateTimeOffset? ExpiresAt = null);
public sealed record RedeemCouponRequest(decimal OrderAmount, string OrderRef);
public sealed record BatchEventResult(string EventId, bool Success, IngestResult? Result, string? Code, int Status);

internal static class EndpointChecks
{
    internal static void Require(bool condition, string code, string message, int status = 422)
    {
        if (!condition) throw new DomainException(code, message, status);
    }

    internal static async Task RequireMemberAsync(LoyaltyDbContext db, Guid id, CancellationToken ct)
    {
        Require(await db.Members.AnyAsync(x => x.Id == id, ct), "member_not_found", "Member was not found.", 404);
    }

    internal static async Task RequireCampaignAsync(LoyaltyDbContext db, Guid id, CancellationToken ct)
    {
        Require(await db.Campaigns.AnyAsync(x => x.Id == id, ct), "campaign_not_found", "Campaign was not found.", 404);
    }
}
