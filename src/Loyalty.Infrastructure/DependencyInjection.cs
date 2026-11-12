using Loyalty.Core;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Loyalty.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddLoyaltyInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LoyaltyOptions>().BindConfiguration("Loyalty").ValidateDataAnnotations().ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        var connectionString = configuration.GetConnectionString("Loyalty") ?? "Data Source=appdata/loyalty-engine.db";
        services.AddDbContextFactory<LoyaltyDbContext>(options => options.UseSqlite(connectionString));
        services.AddMemoryCache();
        services.AddSingleton<MutationGate>();
        services.AddSingleton<TransactionRunner>();
        services.AddSingleton<DbLeaderboardStore>();
        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redisOptions = ConfigurationOptions.Parse(redisConnection);
                redisOptions.AbortOnConnectFail = false;
                redisOptions.ConnectRetry = 0;
                redisOptions.ConnectTimeout = 1000;
                redisOptions.AsyncTimeout = 1000;
                return ConnectionMultiplexer.Connect(redisOptions);
            });
            services.AddSingleton<RedisLeaderboardStore>();
            services.AddSingleton<ILeaderboardStore>(provider => new CachedLeaderboardStore(
                new ResilientLeaderboardStore(provider.GetRequiredService<RedisLeaderboardStore>(), provider.GetRequiredService<DbLeaderboardStore>(),
                    provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<ResilientLeaderboardStore>>()),
                provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), provider.GetRequiredService<TimeProvider>()));
        }
        else
            services.AddSingleton<ILeaderboardStore>(provider => new CachedLeaderboardStore(provider.GetRequiredService<DbLeaderboardStore>(),
                provider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<EventIngestionService>();
        services.AddSingleton<CashbackClaimService>();
        services.AddSingleton<TierReviewService>();
        services.AddSingleton<CouponService>();
        services.AddSingleton<DemoDataSeeder>();
        services.AddSingleton<LeaderboardRebuildService>();
        services.AddHostedService<TierReviewWorker>();
        services.AddHostedService<CouponExpiryWorker>();
        return services;
    }

    public static async Task InitializeLoyaltyDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<LoyaltyDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var connection = new SqliteConnectionStringBuilder(db.Database.GetConnectionString());
        if (connection.DataSource is not "" and not ":memory:")
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(connection.DataSource));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }
}
