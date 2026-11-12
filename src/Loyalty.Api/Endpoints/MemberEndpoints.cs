using Loyalty.Core;
using Loyalty.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Loyalty.Api.Endpoints;

public static class MemberEndpoints
{
    public static RouteGroupBuilder MapMembers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/members").WithTags("Members");
        group.MapPost("", async (CreateMemberRequest request, TransactionRunner transactions,
            IOptions<LoyaltyOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            EndpointChecks.Require(!string.IsNullOrWhiteSpace(request.ExternalId) && request.ExternalId.Length <= 128
                && !string.IsNullOrWhiteSpace(request.DisplayName) && request.DisplayName.Length <= 128,
                "invalid_member", "An external ID and display name of at most 128 characters are required.");
            var member = await transactions.RunAsync(async (db, token) =>
            {
                EndpointChecks.Require(!await db.Members.AnyAsync(x => x.ExternalId == request.ExternalId, token),
                    "external_id_exists", "The external member ID already exists.", 409);
                var now = time.GetUtcNow();
                var entity = new Member { Id = Guid.NewGuid(), ExternalId = request.ExternalId, DisplayName = request.DisplayName,
                    TierCode = options.Value.Tiers.MinBy(x => x.Value).Key, TierSince = now, CreatedAt = now };
                db.Members.Add(entity);
                db.Wallets.Add(new Wallet { MemberId = entity.Id });
                return entity;
            }, ct);
            return Results.Created($"/api/members/{member.Id}", member);
        }).WithSummary("Register a member and an empty wallet");

        group.MapGet("/{id:guid}", async (Guid id, LoyaltyDbContext db, IOptions<LoyaltyOptions> options,
            TimeProvider time, CancellationToken ct) =>
        {
            var member = await db.Members.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new DomainException("member_not_found", "Member was not found.", 404);
            var wallet = await db.Wallets.AsNoTracking().SingleAsync(x => x.MemberId == id, ct);
            var now = time.GetUtcNow();
            var spend = await SpendQueries.RollingSpendAsync(db, id, now, options.Value.RollingWindowDays, ct);
            var next = options.Value.Tiers.OrderBy(x => x.Value).SkipWhile(x => x.Key != member.TierCode).Skip(1).FirstOrDefault();
            var progress = next.Key is null ? new NextTierProgress(null, null, 0m)
                : new NextTierProgress(next.Key, next.Value, Math.Max(0m, next.Value - spend));
            return Results.Ok(new { member.Id, member.ExternalId, member.DisplayName, member.TierCode, member.TierSince,
                member.DowngradeWarningAt, RollingSpend = spend, NextTierProgress = progress,
                wallet.ClaimableCashback, wallet.Balance });
        }).WithSummary("Read membership progress and wallet balances");

        group.MapGet("/{id:guid}/tier-history", async (Guid id, LoyaltyDbContext db, CancellationToken ct) =>
        {
            await EndpointChecks.RequireMemberAsync(db, id, ct);
            return Results.Ok(await db.TierChanges.AsNoTracking().Where(x => x.MemberId == id)
                .OrderByDescending(x => x.At).ThenBy(x => x.Id).ToArrayAsync(ct));
        }).WithSummary("Read tier changes and warnings");

        group.MapGet("/{id:guid}/ledger", async (Guid id, int? page, LoyaltyDbContext db, CancellationToken ct) =>
        {
            var number = page ?? 1;
            EndpointChecks.Require(number is > 0 and <= 1_000_000, "invalid_page", "Page must be between 1 and 1000000.", 400);
            await EndpointChecks.RequireMemberAsync(db, id, ct);
            var items = await db.LedgerEntries.AsNoTracking().Where(x => x.MemberId == id)
                .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((number - 1) * 50).Take(50).ToArrayAsync(ct);
            return Results.Ok(new { Page = number, Items = items });
        }).WithSummary("Read a page of 50 ledger entries");

        group.MapPost("/{id:guid}/cashback/claim", async (Guid id, HttpContext context,
            CashbackClaimService claims, CancellationToken ct) =>
        {
            var keys = context.Request.Headers["Idempotency-Key"];
            EndpointChecks.Require(keys.Count == 1 && !string.IsNullOrWhiteSpace(keys[0]),
                "idempotency_key_required", "A single Idempotency-Key header is required.", 400);
            var response = await claims.ClaimAsync(id, keys.ToString(), ct);
            if (response.Replay) context.Response.Headers["Idempotent-Replay"] = "true";
            return Results.Content(response.ResultJson, "application/json", statusCode: 200);
        }).WithSummary("Claim cashback within the UTC daily cap");
        return group;
    }
}
