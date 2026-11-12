using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loyalty.Core;
using Microsoft.EntityFrameworkCore;

namespace Loyalty.Tests;

public sealed class RefundAndReviewIntegrationTests
{
    [Theory]
    [InlineData("missing", "original_event_required")]
    [InlineData("unknown", "invalid_original_event")]
    [InlineData("member", "invalid_original_event")]
    [InlineData("category", "invalid_original_event")]
    [InlineData("amount", "refund_exceeds_purchase")]
    public async Task IngestAsync_InvalidRefund_DoesNotChangeWalletOrLedger(string scenario, string expectedCode)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        var owner = scenario == "member" ? await ApiTestRequests.CreateMemberAsync(client, ct) : member;
        using var purchase = await ApiTestRequests.EventAsync(client, owner, "original", 100m, factory.Time.GetUtcNow(), ct);
        await ApiTestRequests.ReadSuccessAsync(purchase, ct);
        var original = scenario switch { "missing" => null, "unknown" => "not-found", _ => "original" };

        using var response = await ApiTestRequests.EventAsync(client, member, "rejected-refund", scenario == "amount" ? 101m : 25m,
            factory.Time.GetUtcNow(), ct, "Refund", original, scenario == "category" ? "other" : "general");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        await using var db = await factory.CreateDbContextAsync(ct);
        Assert.Single(await db.ActivityEvents.ToListAsync(ct));
        Assert.Single(await db.LedgerEntries.ToListAsync(ct));
        Assert.Equal(0.50m, (await db.Wallets.SingleAsync(x => x.MemberId == owner, ct)).ClaimableCashback);
        if (owner != member) Assert.Equal(0m, (await db.Wallets.SingleAsync(x => x.MemberId == member, ct)).ClaimableCashback);
    }

    [Fact]
    public async Task IngestAsync_CumulativeRefundExceedsPurchase_RejectsWithoutPartialWrites()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        using var purchase = await ApiTestRequests.EventAsync(client, member, "purchase", 100m, factory.Time.GetUtcNow(), ct);
        await ApiTestRequests.ReadSuccessAsync(purchase, ct);
        using var firstRefund = await ApiTestRequests.EventAsync(client, member, "refund-one", 60m, factory.Time.GetUtcNow(), ct, "Refund", "purchase");
        await ApiTestRequests.ReadSuccessAsync(firstRefund, ct);

        using var response = await ApiTestRequests.EventAsync(client, member, "refund-two", 41m, factory.Time.GetUtcNow(), ct, "Refund", "purchase");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("refund_exceeds_purchase", (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString());
        await using var db = await factory.CreateDbContextAsync(ct);
        Assert.Equal(2, await db.ActivityEvents.CountAsync(ct));
        Assert.Equal(2, await db.LedgerEntries.CountAsync(ct));
        Assert.Equal(0.20m, (await db.Wallets.SingleAsync(ct)).ClaimableCashback);
    }

    [Fact]
    public async Task IngestAsync_ThreeFractionalRefunds_ReversesExactlyOriginalAccrual()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        using var purchase = await ApiTestRequests.EventAsync(client, member, "fractional-purchase", 3m, factory.Time.GetUtcNow(), ct);
        Assert.Equal(0.02m, (await ApiTestRequests.ReadSuccessAsync(purchase, ct)).GetProperty("cashbackAmount").GetDecimal());

        for (var index = 0; index < 3; index++)
        {
            using var refund = await ApiTestRequests.EventAsync(client, member, $"fractional-refund-{index}", 1m,
                factory.Time.GetUtcNow(), ct, "Refund", "fractional-purchase");
            await ApiTestRequests.ReadSuccessAsync(refund, ct);
        }

        await using var db = await factory.CreateDbContextAsync(ct);
        var ledger = await db.LedgerEntries.ToListAsync(ct);
        Assert.Equal(-0.02m, ledger.Where(x => x.Type == LedgerEntryType.CashbackReversed).Sum(x => x.Amount));
        Assert.Equal(0m, ledger.Sum(x => x.Amount));
        Assert.Equal(0m, (await db.Wallets.SingleAsync(ct)).ClaimableCashback);
    }

    [Fact]
    public async Task IngestAsync_RefundAfterClaim_PreservesFullReversalWithoutNegativeClaimable()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        using var purchase = await ApiTestRequests.EventAsync(client, member, "purchase", 100m, factory.Time.GetUtcNow(), ct);
        await ApiTestRequests.ReadSuccessAsync(purchase, ct);
        using var claim = await ApiTestRequests.ClaimAsync(client, member, "before-refund", ct);
        await ApiTestRequests.ReadSuccessAsync(claim, ct);

        using var refund = await ApiTestRequests.EventAsync(client, member, "refund", 100m, factory.Time.GetUtcNow(), ct, "Refund", "purchase");
        await ApiTestRequests.ReadSuccessAsync(refund, ct);

        await using var db = await factory.CreateDbContextAsync(ct);
        var wallet = await db.Wallets.SingleAsync(ct);
        Assert.Equal(0m, wallet.ClaimableCashback);
        Assert.Equal(0.50m, wallet.Balance);
        Assert.Equal(-0.50m, (await db.LedgerEntries.SingleAsync(x => x.Type == LedgerEntryType.CashbackReversed, ct)).Amount);
    }

    [Fact]
    public async Task RunAsync_AgedPurchaseAndElapsedGrace_DowngradesOneTierPerGracePeriod()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        using var purchase = await ApiTestRequests.EventAsync(client, member, "qualifying-purchase", 20000m, factory.Time.GetUtcNow(), ct);
        Assert.Equal("Platinum", (await ApiTestRequests.ReadSuccessAsync(purchase, ct)).GetProperty("tierCode").GetString());
        factory.Time.Advance(TimeSpan.FromDays(31));

        using var warn = await client.PostAsync("/api/admin/tier-review/run", null, ct);
        Assert.Equal(1, (await ApiTestRequests.ReadSuccessAsync(warn, ct)).GetProperty("warned").GetInt32());
        var warningAt = factory.Time.GetUtcNow();
        factory.Time.Advance(TimeSpan.FromDays(13));
        using var withinGrace = await client.PostAsync("/api/admin/tier-review/run", null, ct);
        Assert.Equal(0, (await ApiTestRequests.ReadSuccessAsync(withinGrace, ct)).GetProperty("downgraded").GetInt32());
        factory.Time.Advance(TimeSpan.FromDays(1));
        using var downgrade = await client.PostAsync("/api/admin/tier-review/run", null, ct);
        Assert.Equal(1, (await ApiTestRequests.ReadSuccessAsync(downgrade, ct)).GetProperty("downgraded").GetInt32());
        using var immediateRetry = await client.PostAsync("/api/admin/tier-review/run", null, ct);
        Assert.Equal(0, (await ApiTestRequests.ReadSuccessAsync(immediateRetry, ct)).GetProperty("downgraded").GetInt32());

        await using var db = await factory.CreateDbContextAsync(ct);
        var persisted = await db.Members.SingleAsync(ct);
        Assert.Equal("Gold", persisted.TierCode);
        Assert.Equal(warningAt.AddDays(14), persisted.DowngradeWarningAt);
        var history = await db.TierChanges.ToListAsync(ct);
        Assert.Equal(3, history.Count);
        Assert.Single(history, change => change.Reason == TierChangeReason.Upgrade);
        Assert.Single(history, change => change.Reason == TierChangeReason.DowngradeWarning);
        Assert.Single(history, change => change.Reason == TierChangeReason.Downgrade);
    }

    [Fact]
    public async Task IngestAsync_ActiveCampaignPurchaseAndRefund_UpdatesDatabaseScores()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        using var create = await client.PostAsJsonAsync("/api/campaigns", new { Name = "Sample Campaign", Metric = "Spend",
            StartsAt = factory.Time.GetUtcNow().AddDays(-1), EndsAt = factory.Time.GetUtcNow().AddDays(1), RewardsJson = "[]" }, ct);
        var campaign = (await ApiTestRequests.ReadSuccessAsync(create, ct)).GetProperty("id").GetGuid();
        using var purchase = await ApiTestRequests.EventAsync(client, member, "campaign-purchase", 100m, factory.Time.GetUtcNow(), ct);
        await ApiTestRequests.ReadSuccessAsync(purchase, ct);
        using var refund = await ApiTestRequests.EventAsync(client, member, "campaign-refund", 25m, factory.Time.GetUtcNow(), ct, "Refund", "campaign-purchase");
        await ApiTestRequests.ReadSuccessAsync(refund, ct);

        using var response = await client.GetAsync($"/api/campaigns/{campaign}/leaderboard", ct);
        var board = await ApiTestRequests.ReadSuccessAsync(response, ct);

        Assert.Equal("database", board.GetProperty("source").GetString());
        var entry = Assert.Single(board.GetProperty("entries").EnumerateArray());
        Assert.Equal(75m, entry.GetProperty("score").GetDecimal());
        Assert.Equal("Sample Member", entry.GetProperty("displayName").GetString());
        await using var db = await factory.CreateDbContextAsync(ct);
        Assert.Equal(75m, (await db.CampaignScores.SingleAsync(ct)).Score);
    }
}
