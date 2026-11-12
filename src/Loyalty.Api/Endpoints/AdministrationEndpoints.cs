using Loyalty.Infrastructure;

namespace Loyalty.Api.Endpoints;

public static class AdministrationEndpoints
{
    public static RouteGroupBuilder MapAdministration(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Administration");
        group.MapPost("/admin/tier-review/run", async (DateTimeOffset? asOf, TimeProvider time,
            TierReviewService review, CancellationToken ct) => Results.Ok(await review.RunAsync(asOf ?? time.GetUtcNow(), ct)))
            .WithSummary("Run tier review at the supplied evaluation time");
        if (app.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
            group.MapPost("/dev/seed", async (DemoDataSeeder seed, CancellationToken ct) => Results.Ok(await seed.SeedAsync(ct)))
                .WithSummary("Create or replay synthetic demo data");
        return group;
    }
}
