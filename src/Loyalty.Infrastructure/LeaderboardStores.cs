using System.Collections.Concurrent;
using Loyalty.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Loyalty.Infrastructure;

public sealed class DbLeaderboardStore(IDbContextFactory<LoyaltyDbContext> factory) : ILeaderboardStore
{
    public Task IncrementAsync(Guid campaignId, Guid memberId, decimal delta, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<LeaderboardResult> GetTopAsync(Guid campaignId, int top, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var scores = await db.CampaignScores.AsNoTracking().Where(x => x.CampaignId == campaignId)
            .OrderByDescending(x => x.Score).ThenBy(x => x.UpdatedAt).ThenBy(x => x.MemberId).Take(Math.Max(0, top)).ToListAsync(cancellationToken);
        return new LeaderboardResult(scores.Select((x, i) => new LeaderboardEntry(x.MemberId, i + 1, x.Score)).ToArray(), "database");
    }

    public async Task<MemberRank?> GetRankAsync(Guid campaignId, Guid memberId, CancellationToken cancellationToken = default)
    {
        var result = await GetTopAsync(campaignId, int.MaxValue, cancellationToken);
        var entry = result.Entries.FirstOrDefault(x => x.MemberId == memberId);
        return entry is null ? null : new MemberRank(memberId, entry.Rank, entry.Score, result.Source);
    }
}

/// <summary>Rebuilds after committed writes so retries never replay a delta into an already rebuilt snapshot.</summary>
public sealed class RedisLeaderboardStore(IConnectionMultiplexer connection, IDbContextFactory<LoyaltyDbContext> factory) : ILeaderboardStore, IDisposable
{
    private readonly ConcurrentDictionary<Guid, bool> dirty = new();
    private readonly ConcurrentDictionary<Guid, int> expectedCounts = new();
    private readonly SemaphoreSlim synchronization = new(1, 1);
    private static string Key(Guid campaignId) => $"lb:{campaignId}";

    internal void MarkDirty(Guid campaignId) => dirty[campaignId] = true;

    public void Dispose() => synchronization.Dispose();

    public async Task IncrementAsync(Guid campaignId, Guid memberId, decimal delta, CancellationToken cancellationToken = default)
    {
        await synchronization.WaitAsync(cancellationToken);
        try
        {
            dirty[campaignId] = true;
            await connection.GetDatabase().SortedSetIncrementAsync(Key(campaignId), memberId.ToString(), (double)delta).WaitAsync(cancellationToken);
            await RebuildCoreAsync(campaignId, cancellationToken);
        }
        finally { synchronization.Release(); }
    }

    public async Task<LeaderboardResult> GetTopAsync(Guid campaignId, int top, CancellationToken cancellationToken = default)
    {
        await synchronization.WaitAsync(cancellationToken);
        try
        {
            await EnsureSynchronizedAsync(campaignId, cancellationToken);
            if (top <= 0) return new LeaderboardResult([], "redis");
            var entries = await connection.GetDatabase().SortedSetRangeByRankWithScoresAsync(Key(campaignId), 0, top - 1, Order.Descending).WaitAsync(cancellationToken);
            return new LeaderboardResult(entries.Select((x, i) => new LeaderboardEntry(Guid.Parse(x.Element.ToString()), i + 1, (decimal)x.Score)).ToArray(), "redis");
        }
        finally { synchronization.Release(); }
    }

    public async Task<MemberRank?> GetRankAsync(Guid campaignId, Guid memberId, CancellationToken cancellationToken = default)
    {
        await synchronization.WaitAsync(cancellationToken);
        try
        {
            await EnsureSynchronizedAsync(campaignId, cancellationToken);
            var database = connection.GetDatabase();
            var redisRank = await database.SortedSetRankAsync(Key(campaignId), memberId.ToString(), Order.Descending).WaitAsync(cancellationToken);
            var score = await database.SortedSetScoreAsync(Key(campaignId), memberId.ToString()).WaitAsync(cancellationToken);
            return redisRank is null || score is null ? null : new MemberRank(memberId, checked((int)redisRank.Value + 1), (decimal)score.Value, "redis");
        }
        finally { synchronization.Release(); }
    }

    public async Task RebuildAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        await synchronization.WaitAsync(cancellationToken);
        try { await RebuildCoreAsync(campaignId, cancellationToken); }
        finally { synchronization.Release(); }
    }

    private async Task RebuildCoreAsync(Guid campaignId, CancellationToken cancellationToken) =>
        await ReplaceAsync(campaignId, await ReadScoresAsync(campaignId, cancellationToken), cancellationToken);

    private async Task EnsureSynchronizedAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        if (dirty.GetValueOrDefault(campaignId, true) || !expectedCounts.TryGetValue(campaignId, out var expected))
        {
            await RebuildCoreAsync(campaignId, cancellationToken);
            return;
        }
        var actual = await connection.GetDatabase().SortedSetLengthAsync(Key(campaignId)).WaitAsync(cancellationToken);
        if (actual != expected) await RebuildCoreAsync(campaignId, cancellationToken);
    }

    private async Task<List<CampaignScore>> ReadScoresAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.CampaignScores.AsNoTracking().Where(x => x.CampaignId == campaignId)
            .OrderByDescending(x => x.Score).ThenBy(x => x.UpdatedAt).ThenBy(x => x.MemberId).ToListAsync(cancellationToken);
    }

    private async Task ReplaceAsync(Guid campaignId, List<CampaignScore> scores, CancellationToken cancellationToken)
    {
        dirty[campaignId] = true;
        var database = connection.GetDatabase();
        var key = Key(campaignId);
        if (scores.Count == 0)
            await database.KeyDeleteAsync(key).WaitAsync(cancellationToken);
        else
        {
            var temporary = $"{key}:rebuild:{Guid.NewGuid():N}";
            await database.SortedSetAddAsync(temporary, scores.Select(x => new SortedSetEntry(x.MemberId.ToString(), (double)x.Score)).ToArray()).WaitAsync(cancellationToken);
            await database.KeyExpireAsync(temporary, TimeSpan.FromMinutes(5)).WaitAsync(cancellationToken);
            await database.KeyRenameAsync(temporary, key).WaitAsync(cancellationToken);
            await database.KeyPersistAsync(key).WaitAsync(cancellationToken);
        }
        expectedCounts[campaignId] = scores.Count;
        dirty[campaignId] = false;
    }
}

public sealed class ResilientLeaderboardStore(ILeaderboardStore primary, ILeaderboardStore fallback,
    TimeProvider time, ILogger<ResilientLeaderboardStore> logger) : ILeaderboardStore, IDisposable
{
    private long circuitUntil;
    private readonly SemaphoreSlim probeGate = new(1, 1);

    public void Dispose() => probeGate.Dispose();

    public async Task IncrementAsync(Guid campaignId, Guid memberId, decimal delta, CancellationToken cancellationToken = default)
    {
        if (IsOpen) { MarkDirty(campaignId); return; }
        await probeGate.WaitAsync(cancellationToken);
        try
        {
            if (IsOpen) { MarkDirty(campaignId); return; }
            try { await primary.IncrementAsync(campaignId, memberId, delta, cancellationToken); }
            catch (Exception exception) when (IsRedisFailure(exception)) { OpenCircuit(exception); MarkDirty(campaignId); }
        }
        finally { probeGate.Release(); }
    }

    public Task<LeaderboardResult> GetTopAsync(Guid campaignId, int top, CancellationToken cancellationToken = default) =>
        ReadAsync(campaignId, ct => primary.GetTopAsync(campaignId, top, ct), ct => fallback.GetTopAsync(campaignId, top, ct), cancellationToken);

    public Task<MemberRank?> GetRankAsync(Guid campaignId, Guid memberId, CancellationToken cancellationToken = default) =>
        ReadAsync(campaignId, ct => primary.GetRankAsync(campaignId, memberId, ct), ct => fallback.GetRankAsync(campaignId, memberId, ct), cancellationToken);

    private bool IsOpen => Interlocked.Read(ref circuitUntil) > time.GetUtcNow().UtcTicks;

    private async Task<T> ReadAsync<T>(Guid campaignId, Func<CancellationToken, Task<T>> readPrimary,
        Func<CancellationToken, Task<T>> readFallback, CancellationToken cancellationToken)
    {
        if (IsOpen) return await readFallback(cancellationToken);
        await probeGate.WaitAsync(cancellationToken);
        try
        {
            if (IsOpen) return await readFallback(cancellationToken);
            try
            {
                var result = await readPrimary(cancellationToken);
                Interlocked.Exchange(ref circuitUntil, 0);
                return result;
            }
            catch (Exception exception) when (IsRedisFailure(exception))
            {
                OpenCircuit(exception);
                MarkDirty(campaignId);
                return await readFallback(cancellationToken);
            }
        }
        finally { probeGate.Release(); }
    }

    private void MarkDirty(Guid campaignId)
    {
        if (primary is RedisLeaderboardStore redis) redis.MarkDirty(campaignId);
    }

    private void OpenCircuit(Exception exception)
    {
        Interlocked.Exchange(ref circuitUntil, time.GetUtcNow().AddSeconds(30).UtcTicks);
        InfrastructureLog.CacheUnavailable(logger, exception);
    }

    private static bool IsRedisFailure(Exception exception) => exception is RedisException or TimeoutException;
}

public sealed class CachedLeaderboardStore(ILeaderboardStore inner, IMemoryCache cache, TimeProvider time) : ILeaderboardStore, IDisposable
{
    private readonly ConcurrentDictionary<Guid, long> versions = new();
    private sealed record CachedResult(LeaderboardResult Result, DateTimeOffset ExpiresAt);

    public void Dispose()
    {
        if (inner is ResilientLeaderboardStore resilient) resilient.Dispose();
    }

    public async Task IncrementAsync(Guid campaignId, Guid memberId, decimal delta, CancellationToken cancellationToken = default)
    {
        versions.AddOrUpdate(campaignId, 1, (_, version) => version + 1);
        await inner.IncrementAsync(campaignId, memberId, delta, cancellationToken);
        versions.AddOrUpdate(campaignId, 1, (_, version) => version + 1);
    }

    public async Task<LeaderboardResult> GetTopAsync(Guid campaignId, int top, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = (this, campaignId, top, versions.GetValueOrDefault(campaignId));
        if (cache.TryGetValue<CachedResult>(key, out var entry) && entry!.ExpiresAt > time.GetUtcNow()) return entry.Result;
        var result = await inner.GetTopAsync(campaignId, top, cancellationToken);
        cache.Set(key, new CachedResult(result, time.GetUtcNow().AddSeconds(5)), TimeSpan.FromSeconds(5));
        return result;
    }

    public Task<MemberRank?> GetRankAsync(Guid campaignId, Guid memberId, CancellationToken cancellationToken = default) =>
        inner.GetRankAsync(campaignId, memberId, cancellationToken);

    internal void Invalidate(Guid campaignId) => versions.AddOrUpdate(campaignId, 1, (_, version) => version + 1);
}

public sealed class LeaderboardRebuildService(IServiceProvider services, ILeaderboardStore leaderboard)
{
    public async Task RebuildAsync(Guid campaignId, CancellationToken cancellationToken = default)
    {
        if (services.GetService(typeof(RedisLeaderboardStore)) is RedisLeaderboardStore redis)
            await redis.RebuildAsync(campaignId, cancellationToken);
        if (leaderboard is CachedLeaderboardStore cached) cached.Invalidate(campaignId);
    }
}
