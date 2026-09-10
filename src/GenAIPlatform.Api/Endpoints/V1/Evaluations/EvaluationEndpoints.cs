using GenAIPlatform.Api.Endpoints.V1.Requests;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Evaluations.StartRun;

namespace GenAIPlatform.Api;

internal static class EvaluationEndpoints
{
    public static RouteGroupBuilder MapEvaluationEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/evaluations/runs", StartEvaluationRun)
            .WithName("StartEvaluationRun")
            .WithSummary("Start a new evaluation run against the configured dataset.")
            .Produces<EvaluationRunResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        api.MapGet("/evaluations/runs/{runId:guid}", GetEvaluationRun)
            .WithName("GetEvaluationRun")
            .WithSummary("Get the full result (cases + checks) of a previously completed evaluation run.")
            .Produces<EvaluationRunResult>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/evaluations/runs/{runId:guid}/summary", GetEvaluationRunSummary)
            .WithName("GetEvaluationRunSummary")
            .WithSummary("Get a compact summary (counts and headline metrics) of a previously completed evaluation run.")
            .Produces<EvaluationRunSummary>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> StartEvaluationRun(
        StartEvaluationRunHttpRequest request,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var defaults = new StartEvaluationRunCommand();
        var result = await dispatcher.DispatchAsync<StartEvaluationRunCommand, EvaluationRunResult>(
            new StartEvaluationRunCommand(
                DatasetVersion: request.DatasetVersion,
                Model: request.Model ?? defaults.Model,
                Temperature: request.Temperature ?? defaults.Temperature,
                MaxOutputTokens: request.MaxOutputTokens ?? defaults.MaxOutputTokens,
                TopK: request.TopK,
                MinSimilarityScore: request.MinSimilarityScore,
                CorrelationId: request.CorrelationId),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> GetEvaluationRun(
        Guid runId,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchAsync<GetEvaluationRunQuery, EvaluationRunResult?>(
            new GetEvaluationRunQuery(runId),
            cancellationToken);

        return result is null
            ? ApiErrorMapping.NotFound("evaluation run was not found")
            : Results.Ok(result);
    }

    private static async Task<IResult> GetEvaluationRunSummary(
        Guid runId,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchAsync<GetEvaluationSummaryQuery, EvaluationRunSummary?>(
            new GetEvaluationSummaryQuery(runId),
            cancellationToken);

        return result is null
            ? ApiErrorMapping.NotFound("evaluation run was not found")
            : Results.Ok(result);
    }
}
