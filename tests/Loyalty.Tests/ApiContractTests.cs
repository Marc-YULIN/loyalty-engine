using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Loyalty.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Loyalty.Tests;

public sealed class ApiContractTests
{
    [Theory]
    [InlineData("/api/members/00000000-0000-0000-0000-000000000001")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar/v1")]
    public async Task GetAsync_MissingApiKey_ReturnsUnauthorizedProblem(string path)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(path, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("invalid_api_key", (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetAsync_HealthWithoutApiKey_ReturnsHealthy()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", cancellation.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_InvalidOrMultipleApiKeys_ReturnsUnauthorized(bool multipleKeys)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", multipleKeys ? ["test-loyalty-key", "test-loyalty-key"] : ["wrong-key"]);

        using var response = await client.GetAsync("/openapi/v1.json", cancellation.Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ClaimAsync_MissingIdempotencyKey_ReturnsBadRequestWithoutMutation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        var member = await ApiTestRequests.CreateMemberAsync(client, ct);

        using var response = await client.PostAsync($"/api/members/{member}/cashback/claim", null, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_required", (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString());
        await using var db = await factory.CreateDbContextAsync(ct);
        Assert.Empty(await db.CashbackClaims.ToListAsync(ct));
        Assert.Empty(await db.LedgerEntries.ToListAsync(ct));
    }

    [Fact]
    public async Task PostAsync_MalformedJson_ReturnsProblemDetails()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var factory = new LoyaltyApiFactory();
        using var client = factory.CreateAuthorizedClient();
        using var body = new StringContent("{", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/members", body, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(400, (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task CreateClient_SeparateFactories_UseIndependentTemporaryDatabases()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cancellation.Token;
        using var first = new LoyaltyApiFactory();
        using var firstClient = first.CreateAuthorizedClient();
        await ApiTestRequests.CreateMemberAsync(firstClient, ct);
        using var second = new LoyaltyApiFactory();
        using var secondClient = second.CreateAuthorizedClient();
        await using var firstDb = await first.CreateDbContextAsync(ct);
        await using var secondDb = await second.CreateDbContextAsync(ct);

        Assert.Equal(first.DatabasePath, new SqliteConnectionStringBuilder(firstDb.Database.GetConnectionString()).DataSource);
        Assert.Equal(second.DatabasePath, new SqliteConnectionStringBuilder(secondDb.Database.GetConnectionString()).DataSource);
        Assert.NotEqual(first.DatabasePath, second.DatabasePath);
        Assert.True(File.Exists(first.DatabasePath));
        Assert.True(File.Exists(second.DatabasePath));
        Assert.Single(await firstDb.Members.ToListAsync(ct));
        Assert.Empty(await secondDb.Members.ToListAsync(ct));
    }
}
