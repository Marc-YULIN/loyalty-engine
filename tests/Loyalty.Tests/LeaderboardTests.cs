using Loyalty.Core;
using Loyalty.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Loyalty.Tests;

public sealed class LeaderboardTests
{
    private static readonly int[] ExpectedRanks = [1, 2, 3, 4];
    private static readonly decimal[] ExpectedScores = [100m, 10m, 10m, 9.9999m];
    [Fact]
    public async Task GetTopAsync_RedisFailure_UsesFallbackUntilCircuitExpiresThenRecovers()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cancellation.Token;
        var time = CreateClock();
        var primary = Substitute.For<ILeaderboardStore>();
        var fallback = Substitute.For<ILeaderboardStore>();
        var campaign = Guid.NewGuid();
        var member = Guid.NewGuid();
        var databaseResult = new LeaderboardResult([new(member, 1, 10m)], "database");
        var redisResult = new LeaderboardResult([new(member, 1, 10m)], "redis");
        primary.GetTopAsync(campaign, 10, ct).Returns(Task.FromException<LeaderboardResult>(ConnectionFailure()));
        fallback.GetTopAsync(campaign, 10, ct).Returns(Task.FromResult(databaseResult));
        fallback.GetRankAsync(campaign, member, ct).Returns(Task.FromResult<MemberRank?>(new(member, 1, 10m, "database")));
        var store = new ResilientLeaderboardStore(primary, fallback, time, NullLogger<ResilientLeaderboardStore>.Instance);

        Assert.Same(databaseResult, await store.GetTopAsync(campaign, 10, ct));
        primary.GetTopAsync(campaign, 10, ct).Returns(Task.FromResult(redisResult));
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Same(databaseResult, await store.GetTopAsync(campaign, 10, ct));
        Assert.Equal("database", (await store.GetRankAsync(campaign, member, ct))!.Source);
        await primary.Received(1).GetTopAsync(campaign, 10, ct);
        await primary.DidNotReceive().GetRankAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Same(redisResult, await store.GetTopAsync(campaign, 10, ct));
        await primary.Received(2).GetTopAsync(campaign, 10, ct);
        await fallback.Received(2).GetTopAsync(campaign, 10, ct);
    }

    [Fact]
    public async Task IncrementAsync_RedisFailure_DoesNotRepeatAuthoritativeDatabaseWrite()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cancellation.Token;
        var time = CreateClock();
        var primary = Substitute.For<ILeaderboardStore>();
        var fallback = Substitute.For<ILeaderboardStore>();
        var campaign = Guid.NewGuid();
        var member = Guid.NewGuid();
        primary.IncrementAsync(campaign, member, 5m, ct).Returns(Task.FromException(ConnectionFailure()));
        var store = new ResilientLeaderboardStore(primary, fallback, time, NullLogger<ResilientLeaderboardStore>.Instance);

        await store.IncrementAsync(campaign, member, 5m, ct);
        await store.IncrementAsync(campaign, member, 5m, ct);
        await primary.Received(1).IncrementAsync(campaign, member, 5m, ct);
        await fallback.DidNotReceive().IncrementAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
        primary.IncrementAsync(campaign, member, 5m, ct).Returns(Task.CompletedTask);
        time.Advance(TimeSpan.FromSeconds(30));

        await store.IncrementAsync(campaign, member, 5m, ct);

        await primary.Received(2).IncrementAsync(campaign, member, 5m, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetTopAsync_NonRedisFailure_PropagatesWithoutFallback(bool cancellationFailure)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cancellation.Token;
        var primary = Substitute.For<ILeaderboardStore>();
        var fallback = Substitute.For<ILeaderboardStore>();
        var campaign = Guid.NewGuid();
        Exception failure = cancellationFailure ? new OperationCanceledException(ct) : new InvalidOperationException("Invalid projection.");
        primary.GetTopAsync(campaign, 10, ct).Returns(Task.FromException<LeaderboardResult>(failure));
        var store = new ResilientLeaderboardStore(primary, fallback, CreateClock(), NullLogger<ResilientLeaderboardStore>.Instance);

        var actual = await Record.ExceptionAsync(() => store.GetTopAsync(campaign, 10, ct));

        Assert.Same(failure, actual);
        await fallback.DidNotReceive().GetTopAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTopAsync_FiveSecondCache_UsesInjectedClockForExpiration()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cancellation.Token;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var time = CreateClock();
        var inner = Substitute.For<ILeaderboardStore>();
        var campaign = Guid.NewGuid();
        var first = new LeaderboardResult([new(Guid.NewGuid(), 1, 10m)], "database");
        var updated = new LeaderboardResult([new(Guid.NewGuid(), 1, 20m)], "database");
        inner.GetTopAsync(campaign, 10, ct).Returns(Task.FromResult(first));
        var store = new CachedLeaderboardStore(inner, cache, time);

        Assert.Same(first, await store.GetTopAsync(campaign, 10, ct));
        inner.GetTopAsync(campaign, 10, ct).Returns(Task.FromResult(updated));
        time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.Same(first, await store.GetTopAsync(campaign, 10, ct));
        await inner.Received(1).GetTopAsync(campaign, 10, ct);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Same(updated, await store.GetTopAsync(campaign, 10, ct));
        await inner.Received(2).GetTopAsync(campaign, 10, ct);
    }

    [Fact]
    public async Task IncrementAsync_OneCampaignChanges_InvalidatesOnlyItsCachedTopSizes()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = cancellation.Token;
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var inner = Substitute.For<ILeaderboardStore>();
        var changed = Guid.NewGuid();
        var unchanged = Guid.NewGuid();
        var result = new LeaderboardResult([], "database");
        inner.GetTopAsync(Arg.Any<Guid>(), Arg.Any<int>(), ct).Returns(Task.FromResult(result));
        var store = new CachedLeaderboardStore(inner, cache, CreateClock());
        await store.GetTopAsync(changed, 10, ct);
        await store.GetTopAsync(changed, 20, ct);
        await store.GetTopAsync(unchanged, 10, ct);

        await store.IncrementAsync(changed, Guid.NewGuid(), 1m, ct);
        await store.GetTopAsync(changed, 10, ct);
        await store.GetTopAsync(changed, 20, ct);
        await store.GetTopAsync(unchanged, 10, ct);

        await inner.Received(2).GetTopAsync(changed, 10, ct);
        await inner.Received(2).GetTopAsync(changed, 20, ct);
        await inner.Received(1).GetTopAsync(unchanged, 10, ct);
        await inner.Received(1).IncrementAsync(changed, Arg.Any<Guid>(), 1m, ct);
    }

    [Fact]
    public async Task GetTopAsync_DatabaseScoresAndTies_OrdersNumericallyThenByEarlierUpdate()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var ids = new List<Guid>();
        for (var index = 0; index < 4; index++) ids.Add(await ApiTestRequests.CreateMemberAsync(client, ct));
        var campaign = Guid.NewGuid();
        var now = factory.Time.GetUtcNow();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            db.Campaigns.Add(new Campaign { Id = campaign, Name = "Sample Ranking", StartsAt = now.AddDays(-1), EndsAt = now.AddDays(1) });
            db.CampaignScores.AddRange(
                new CampaignScore { CampaignId = campaign, MemberId = ids[0], Score = 10m, UpdatedAt = now },
                new CampaignScore { CampaignId = campaign, MemberId = ids[1], Score = 100m, UpdatedAt = now },
                new CampaignScore { CampaignId = campaign, MemberId = ids[2], Score = 10m, UpdatedAt = now.AddMinutes(-1) },
                new CampaignScore { CampaignId = campaign, MemberId = ids[3], Score = 9.9999m, UpdatedAt = now.AddMinutes(-2) });
            await db.SaveChangesAsync(ct);
        }
        var store = factory.Services.GetRequiredService<DbLeaderboardStore>();

        var board = await store.GetTopAsync(campaign, 10, ct);

        Assert.Equal("database", board.Source);
        Assert.Equal(new[] { ids[1], ids[2], ids[0], ids[3] }, board.Entries.Select(x => x.MemberId));
        Assert.Equal(ExpectedRanks, board.Entries.Select(x => x.Rank));
        Assert.Equal(ExpectedScores, board.Entries.Select(x => x.Score));
        Assert.Equal(3, (await store.GetRankAsync(campaign, ids[0], ct))!.Rank);
        Assert.Null(await store.GetRankAsync(campaign, Guid.NewGuid(), ct));
        Assert.Equal(2, (await store.GetTopAsync(campaign, 2, ct)).Entries.Count);
    }

    private static ManualTimeProvider CreateClock() => new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));
    private static RedisConnectionException ConnectionFailure() =>
        new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Cache is unavailable.", null, CommandStatus.Unknown);
}
