using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Usage.GetUsage;

namespace GenAIPlatform.Api;

internal static class UsageEndpoints
{
    public static RouteGroupBuilder MapUsageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/usage", GetUsage)
            .WithName("GetUsage")
            .WithSummary("Get aggregated AI request usage metrics (tokens, cost, latency) filtered by optional dimensions.")
            .Produces<UsageSummary>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<IResult> GetUsage(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? userId,
        string? tenantId,
        string? model,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchAsync<UsageQuery, UsageSummary>(
            new UsageQuery(from, to, userId, tenantId, model),
            cancellationToken);

        return Results.Ok(result);
    }
}
