using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Loyalty.Core;
using Loyalty.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Loyalty.Tests;

public sealed class TransactionIntegrationTests
{
    [Fact]
    public async Task IngestAsync_DuplicateEvent_AccruesOnce()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        using var first = await ApiTestRequests.EventAsync(client, member, "purchase-duplicate", 100m, factory.Time.GetUtcNow(), ct);
        using var repeat = await ApiTestRequests.EventAsync(client, member, "purchase-duplicate", 100m, factory.Time.GetUtcNow(), ct);

        Assert.False((await ApiTestRequests.ReadSuccessAsync(first, ct)).GetProperty("duplicate").GetBoolean());
        Assert.True((await ApiTestRequests.ReadSuccessAsync(repeat, ct)).GetProperty("duplicate").GetBoolean());
        await using var db = await factory.CreateDbContextAsync(ct);
        Assert.Single(await db.ActivityEvents.ToListAsync(ct));
        Assert.Single(await db.LedgerEntries.ToListAsync(ct));
        Assert.Equal(0.50m, (await db.Wallets.SingleAsync(ct)).ClaimableCashback);
    }

    [Theory]
    [InlineData(75, 20)]
    [InlineData(7, 7)]
    public async Task ClaimAsync_TwentyDistinctConcurrentKeys_RespectsCapAndLedger(int claimable, int expected)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        await SeedWalletAsync(factory, member, claimable, ct);

        var responses = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(index => ApiTestRequests.ClaimAsync(client, member, $"parallel-{index}", ct)));
        try
        {
            var results = await Task.WhenAll(responses.Select(response => ApiTestRequests.ReadSuccessAsync(response, ct)));
            Assert.Equal(expected, results.Sum(result => result.GetProperty("claimedAmount").GetDecimal()));
            await using var db = await factory.CreateDbContextAsync(ct);
            var wallet = await db.Wallets.SingleAsync(ct);
            var claims = await db.CashbackClaims.ToListAsync(ct);
            var ledger = await db.LedgerEntries.Where(x => x.Type == LedgerEntryType.CashbackClaimed).ToListAsync(ct);
            Assert.Equal(20, claims.Count);
            Assert.Equal(expected, claims.Sum(x => x.ClaimedAmount));
            Assert.Equal(expected, ledger.Sum(x => x.Amount));
            Assert.Equal(expected, wallet.Balance);
            Assert.Equal(claimable - expected, wallet.ClaimableCashback);
            Assert.Equal(wallet.Balance, claims.Max(x => x.ClaimedAmount));
            Assert.All(ledger, entry => Assert.InRange(entry.ClaimableAfter, 0m, claimable));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ClaimAsync_TenConcurrentSameKeys_ReplaysIdenticalResponseOnce()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        await SeedWalletAsync(factory, member, 30m, ct);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => ApiTestRequests.ClaimAsync(client, member, "same-key", ct)));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync(ct)));
            Assert.Single(bodies.Distinct(StringComparer.Ordinal));
            Assert.Equal(9, responses.Count(response => response.Headers.TryGetValues("Idempotent-Replay", out var values) && values.Single() == "true"));
            await using var db = await factory.CreateDbContextAsync(ct);
            var claim = Assert.Single(await db.CashbackClaims.ToListAsync(ct));
            Assert.Equal(bodies[0], claim.ResultJson);
            Assert.Single(await db.LedgerEntries.Where(x => x.Type == LedgerEntryType.CashbackClaimed).ToListAsync(ct));
            var wallet = await db.Wallets.SingleAsync(ct);
            Assert.Equal(20m, wallet.Balance);
            Assert.Equal(10m, wallet.ClaimableCashback);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task ClaimAsync_UtcDayChanges_ResetsDailyAllowance()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        await SeedWalletAsync(factory, member, 50m, ct);
        using var first = await ApiTestRequests.ClaimAsync(client, member, "today", ct);
        Assert.Equal(20m, (await ApiTestRequests.ReadSuccessAsync(first, ct)).GetProperty("claimedAmount").GetDecimal());
        using var capped = await ApiTestRequests.ClaimAsync(client, member, "today-again", ct);
        Assert.Equal(0m, (await ApiTestRequests.ReadSuccessAsync(capped, ct)).GetProperty("claimedAmount").GetDecimal());

        factory.Time.Advance(TimeSpan.FromHours(12));
        using var nextDay = await ApiTestRequests.ClaimAsync(client, member, "tomorrow", ct);

        Assert.Equal(20m, (await ApiTestRequests.ReadSuccessAsync(nextDay, ct)).GetProperty("claimedAmount").GetDecimal());
    }

    [Fact]
    public async Task RedeemAsync_TenConcurrentRequests_RedeemsAndWritesLedgerOnce()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);
        var template = await CreateTemplateAsync(client, ct);
        using var issue = await client.PostAsJsonAsync("/api/coupon-batches", new { TemplateId = template, Target = new { MemberIds = new[] { member } } }, ct);
        await ApiTestRequests.ReadSuccessAsync(issue, ct);
        Guid coupon;
        await using (var db = await factory.CreateDbContextAsync(ct)) coupon = (await db.MemberCoupons.SingleAsync(ct)).Id;

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => client.PostAsJsonAsync(
            $"/api/members/{member}/coupons/{coupon}/redeem", new { OrderAmount = 100m, OrderRef = $"order-{index}" }, ct)));
        try
        {
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Equal(9, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            await using var db = await factory.CreateDbContextAsync(ct);
            var persisted = await db.MemberCoupons.SingleAsync(ct);
            Assert.Equal(CouponStatus.Redeemed, persisted.Status);
            Assert.Equal(factory.Time.GetUtcNow(), persisted.RedeemedAt);
            Assert.NotNull(persisted.RedeemedOrderRef);
            var entry = Assert.Single(await db.LedgerEntries.Where(x => x.Type == LedgerEntryType.CouponRedeemed).ToListAsync(ct));
            Assert.Equal(5m, entry.Amount);
            Assert.Equal(coupon.ToString(), entry.RefId);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IssueBatchAsync_RepeatedNormalizedTarget_DoesNotIssueAgain(bool explicitIdentity)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var firstMember = await ApiTestRequests.CreateMemberAsync(client, ct);
        var secondMember = await ApiTestRequests.CreateMemberAsync(client, ct);
        var template = await CreateTemplateAsync(client, ct);
        var batchId = explicitIdentity ? Guid.NewGuid() : (Guid?)null;
        using var first = await client.PostAsJsonAsync("/api/coupon-batches", new { TemplateId = template,
            Target = new { MemberIds = new[] { firstMember, secondMember, firstMember }, BatchId = batchId } }, ct);
        var original = await ApiTestRequests.ReadSuccessAsync(first, ct);

        using var repeat = await client.PostAsJsonAsync("/api/coupon-batches", new { TemplateId = template,
            Target = new { MemberIds = new[] { secondMember, firstMember }, BatchId = batchId } }, ct);
        var replay = await ApiTestRequests.ReadSuccessAsync(repeat, ct);

        Assert.Equal(2, original.GetProperty("issuedCount").GetInt32());
        Assert.Equal(0, replay.GetProperty("issuedCount").GetInt32());
        Assert.Equal(2, replay.GetProperty("skippedCount").GetInt32());
        Assert.Equal(original.GetProperty("batchId").GetGuid(), replay.GetProperty("batchId").GetGuid());
        await using var db = await factory.CreateDbContextAsync(ct);
        Assert.Single(await db.CouponBatches.ToListAsync(ct));
        Assert.Equal(2, await db.MemberCoupons.CountAsync(ct));
        Assert.Equal(2, (await db.MemberCoupons.Select(x => x.MemberId).ToListAsync(ct)).Distinct().Count());
    }

    private static async Task SeedWalletAsync(LoyaltyApiFactory factory, Guid memberId, decimal claimable, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var wallet = await db.Wallets.SingleAsync(x => x.MemberId == memberId, ct);
        wallet.ClaimableCashback = claimable;
        db.LedgerEntries.Add(new LedgerEntry { Id = Guid.NewGuid(), MemberId = memberId, Type = LedgerEntryType.CashbackAccrued,
            Amount = claimable, ClaimableAfter = claimable, CreatedAt = factory.Time.GetUtcNow(), RefId = "opening-accrual" });
        await db.SaveChangesAsync(ct);
    }

    private static async Task<Guid> CreateTemplateAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/coupon-templates", new { Name = "Sample Discount", DiscountType = "FixedAmount",
            Value = 5m, MinSpend = 20m, ValidDays = 7 }, ct);
        return (await ApiTestRequests.ReadSuccessAsync(response, ct)).GetProperty("id").GetGuid();
    }
}
