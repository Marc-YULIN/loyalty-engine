using System.Net.Http.Json;
using System.Text.Json;
using Loyalty.Core;
using Loyalty.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Loyalty.Tests;

public sealed class LoyaltyApiFactory : WebApplicationFactory<Program>
{
    private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"loyalty-tests-{Guid.NewGuid():N}.db");
    public string DatabasePath => databasePath;
    public ManualTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 12, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.UseSetting("ConnectionStrings:Loyalty", $"Data Source={databasePath};Pooling=False");
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:ApiKey"] = "test-loyalty-key",
            ["ConnectionStrings:Loyalty"] = $"Data Source={databasePath};Pooling=False",
            ["ConnectionStrings:Redis"] = "",
            ["Loyalty:EnableTierReviewWorker"] = "false"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            var workers = services.Where(x => x.ServiceType == typeof(IHostedService)
                && x.ImplementationType?.Assembly == typeof(LoyaltyDbContext).Assembly).ToArray();
            foreach (var worker in workers) services.Remove(worker);
        });
    }

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-loyalty-key");
        return client;
    }

    public Task<LoyaltyDbContext> CreateDbContextAsync(CancellationToken cancellationToken) =>
        Services.GetRequiredService<IDbContextFactory<LoyaltyDbContext>>().CreateDbContextAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(databasePath + suffix);
    }
}

internal static class ApiTestRequests
{
    public static async Task<Guid> CreateMemberAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("/api/members", new { ExternalId = Guid.NewGuid().ToString("N"), DisplayName = "Sample Member" }, cancellationToken);
        var result = await ReadSuccessAsync(response, cancellationToken);
        return result.GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> EventAsync(HttpClient client, Guid memberId, string eventId, decimal amount,
        DateTimeOffset occurredAt, CancellationToken cancellationToken, string type = "Purchase", string? originalEventId = null, string category = "general") =>
        client.PostAsJsonAsync("/api/events", new { EventId = eventId, MemberId = memberId, Type = type,
            Category = category, Amount = amount, OccurredAt = occurredAt, OriginalEventId = originalEventId }, cancellationToken);

    public static async Task<HttpResponseMessage> ClaimAsync(HttpClient client, Guid memberId, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/members/{memberId}/cashback/claim");
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request, cancellationToken);
    }

    public static async Task<JsonElement> ReadSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"Expected success, received {(int)response.StatusCode}: {content}");
        return JsonSerializer.Deserialize<JsonElement>(content);
    }
}
