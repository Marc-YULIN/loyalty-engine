using Loyalty.Core;
using Loyalty.Infrastructure;

namespace Loyalty.Api.Endpoints;

public static class EventEndpoints
{
    public static RouteGroupBuilder MapEvents(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/events").WithTags("Events");
        group.MapPost("", async (ActivityEventDto request, EventIngestionService ingestion, CancellationToken ct) =>
            Results.Ok(await ingestion.IngestAsync(request, ct))).WithSummary("Ingest an idempotent purchase or refund");
        group.MapPost("/batch", async (ActivityEventDto[] requests, EventIngestionService ingestion, CancellationToken ct) =>
        {
            EndpointChecks.Require(requests.Length is > 0 and <= 500, "invalid_batch_size", "A batch must contain 1 to 500 events.", 400);
            var results = new List<BatchEventResult>(requests.Length);
            foreach (var request in requests)
            {
                if (request is null)
                {
                    results.Add(new BatchEventResult("", false, null, "invalid_event", 422));
                    continue;
                }
                try { results.Add(new BatchEventResult(request.EventId, true, await ingestion.IngestAsync(request, ct), null, 200)); }
                catch (DomainException exception) { results.Add(new BatchEventResult(request.EventId, false, null, exception.Code, exception.StatusCode)); }
            }
            return Results.Ok(results);
        }).WithSummary("Ingest up to 500 events with individual results");
        return group;
    }
}
