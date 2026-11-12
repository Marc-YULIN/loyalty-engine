using System.Text.Json;
using Loyalty.Core;
using Loyalty.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Loyalty.Api.Endpoints;

public static class CampaignEndpoints
{
    public static RouteGroupBuilder MapCampaigns(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/campaigns").WithTags("Campaigns");
        group.MapPost("", async (CreateCampaignRequest request, TransactionRunner transactions, CancellationToken ct) =>
        {
            EndpointChecks.Require(!string.IsNullOrWhiteSpace(request.Name) && request.Name.Length <= 128
                && Enum.IsDefined(request.Metric) && request.EndsAt > request.StartsAt,
                "invalid_campaign", "A name, supported metric, and increasing campaign dates are required.");
            try { using var rewards = JsonDocument.Parse(request.RewardsJson); }
            catch (Exception exception) when (exception is JsonException or ArgumentNullException)
            { throw new DomainException("invalid_rewards", "RewardsJson must contain valid JSON."); }
            var campaign = new Campaign { Id = Guid.NewGuid(), Name = request.Name, Metric = request.Metric,
                StartsAt = request.StartsAt, EndsAt = request.EndsAt, RewardsJson = request.RewardsJson };
            await transactions.RunAsync((db, token) => { db.Campaigns.Add(campaign); return Task.FromResult(true); }, ct);
            return Results.Created($"/api/campaigns/{campaign.Id}/leaderboard", campaign);
        }).WithSummary("Create a leaderboard campaign");

        group.MapGet("/{id:guid}/leaderboard", async (Guid id, int? top, LoyaltyDbContext db,
            ILeaderboardStore leaderboard, CancellationToken ct) =>
        {
            var count = top ?? 10;
            EndpointChecks.Require(count is > 0 and <= 100, "invalid_top", "Top must be between 1 and 100.", 400);
            await EndpointChecks.RequireCampaignAsync(db, id, ct);
            var result = await leaderboard.GetTopAsync(id, count, ct);
            var memberIds = result.Entries.Select(x => x.MemberId).ToArray();
            var names = await db.Members.AsNoTracking().Where(x => memberIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
            return Results.Ok(new { Entries = result.Entries.Select(x => new { x.Rank, x.MemberId,
                DisplayName = names.GetValueOrDefault(x.MemberId, "Unknown member"), x.Score }), result.Source });
        }).WithSummary("Read ranked members and the data source");

        group.MapGet("/{id:guid}/members/{memberId:guid}/rank", async (Guid id, Guid memberId, LoyaltyDbContext db,
            ILeaderboardStore leaderboard, CancellationToken ct) =>
        {
            await EndpointChecks.RequireCampaignAsync(db, id, ct);
            await EndpointChecks.RequireMemberAsync(db, memberId, ct);
            return Results.Ok(await leaderboard.GetRankAsync(id, memberId, ct));
        }).WithSummary("Read a member rank, or null when unranked");

        group.MapPost("/{id:guid}/rebuild", async (Guid id, LoyaltyDbContext db, LeaderboardRebuildService rebuild, CancellationToken ct) =>
        {
            await EndpointChecks.RequireCampaignAsync(db, id, ct);
            await rebuild.RebuildAsync(id, ct);
            return Results.Ok(new { Rebuilt = true });
        }).WithSummary("Rebuild the Redis projection from database scores");
        return group;
    }
}
