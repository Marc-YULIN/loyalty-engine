using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Loyalty.Api.Endpoints;
using Loyalty.Core;
using Loyalty.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("code", "http_error"));
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOptions<SecurityOptions>().BindConfiguration("Security").ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddLoyaltyInfrastructure(builder.Configuration);
var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, code, detail) = exception switch
    {
        DomainException domain => (domain.StatusCode, domain.Code, domain.Message),
        BadHttpRequestException bad => (bad.StatusCode, "invalid_request", "The request could not be read."),
        _ => (500, "internal_error", "An unexpected error occurred.")
    };
    context.Response.StatusCode = status;
    await Results.Problem(statusCode: status, title: code, detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(context);
}));
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    // Only business endpoints are protected; /health, /openapi and /scalar stay public so the API can be explored.
    if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
    {
        var expected = context.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.ApiKey;
        var supplied = context.Request.Headers["X-Api-Key"];
        if (supplied.Count != 1 || !CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied.ToString())), SHA256.HashData(Encoding.UTF8.GetBytes(expected))))
        {
            await Results.Problem(statusCode: 401, title: "Unauthorized", extensions:
                new Dictionary<string, object?> { ["code"] = "invalid_api_key" }).ExecuteAsync(context);
            return;
        }
    }
    await next(context);
});
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health");
app.MapMembers();
app.MapEvents();
app.MapCampaigns();
app.MapCoupons();
app.MapAdministration();
await app.Services.InitializeLoyaltyDatabaseAsync(app.Lifetime.ApplicationStopping);
if (app.Environment.IsDevelopment())
    await app.Services.GetRequiredService<DemoDataSeeder>().SeedAsync(app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program;
