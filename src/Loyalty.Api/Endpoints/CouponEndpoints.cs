using Loyalty.Core;
using Loyalty.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Loyalty.Api.Endpoints;

public static class CouponEndpoints
{
    public static RouteGroupBuilder MapCoupons(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Coupons");
        group.MapPost("/coupon-templates", async (CreateCouponTemplateRequest request, TransactionRunner transactions, CancellationToken ct) =>
        {
            MoneyStorage.Validate(request.Value);
            MoneyStorage.Validate(request.MinSpend);
            if (request.MaxDiscount is { } maximum) MoneyStorage.Validate(maximum);
            EndpointChecks.Require(!string.IsNullOrWhiteSpace(request.Name) && request.Name.Length <= 128
                && Enum.IsDefined(request.DiscountType) && request.Value > 0 && request.MinSpend >= 0
                && (request.MaxDiscount is null or > 0) && request.ValidDays is > 0 and <= 3650
                && (request.DiscountType != DiscountType.Percentage || request.Value <= 1),
                "invalid_template", "Template values, limits, and validity days must be valid; percentage values are fractions up to one.");
            var template = new CouponTemplate { Id = Guid.NewGuid(), Name = request.Name, DiscountType = request.DiscountType,
                Value = request.Value, MinSpend = request.MinSpend, MaxDiscount = request.MaxDiscount, ValidDays = request.ValidDays };
            await transactions.RunAsync((db, token) => { db.CouponTemplates.Add(template); return Task.FromResult(true); }, ct);
            return Results.Created($"/api/coupon-templates/{template.Id}", template);
        }).WithSummary("Create a fixed or percentage coupon template");

        group.MapPost("/coupon-batches", async (IssueCouponsRequest request, CouponService coupons, CancellationToken ct) =>
        {
            EndpointChecks.Require(request.Target is not null, "invalid_coupon_target", "A coupon target is required.");
            return Results.Ok(await coupons.IssueBatchAsync(request.TemplateId, request.Target!, request.ExpiresAt, ct));
        }).WithSummary("Issue one coupon per targeted member, safely retrying a batch");

        group.MapGet("/members/{id:guid}/coupons", async (Guid id, string? status, LoyaltyDbContext db,
            CouponService coupons, CancellationToken ct) =>
        {
            CouponStatus? filter = null;
            if (status is not null)
            {
                EndpointChecks.Require(Enum.TryParse<CouponStatus>(status, true, out var parsed) && Enum.IsDefined(parsed),
                    "invalid_coupon_status", "Coupon status is not supported.", 400);
                filter = parsed;
            }
            await EndpointChecks.RequireMemberAsync(db, id, ct);
            await coupons.ExpireAsync(ct);
            var query = db.MemberCoupons.AsNoTracking().Where(x => x.MemberId == id);
            if (filter is not null) query = query.Where(x => x.Status == filter);
            return Results.Ok(await query.OrderBy(x => x.ExpiresAt).ThenBy(x => x.Id).Select(x => new {
                x.Id, x.BatchId, x.MemberId, x.TemplateId, x.Template.Name, x.Template.DiscountType, x.Template.Value,
                x.Template.MinSpend, x.Template.MaxDiscount, x.Status, x.ExpiresAt, x.RedeemedAt, x.RedeemedOrderRef }).ToArrayAsync(ct));
        }).WithSummary("Read a member coupon box, optionally filtered by status");

        group.MapPost("/members/{id:guid}/coupons/{couponId:guid}/redeem", async (Guid id, Guid couponId,
            RedeemCouponRequest request, CouponService coupons, CancellationToken ct) =>
            Results.Ok(await coupons.RedeemAsync(id, couponId, request.OrderAmount, request.OrderRef, ct)))
            .WithSummary("Atomically redeem an available, unexpired coupon");
        return group;
    }
}
