using GenAIPlatform.Api.Endpoints.V1.Requests;
using GenAIPlatform.Application.Agentic.Chat;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Generation.Chat;

namespace GenAIPlatform.Api;

internal static class ChatEndpoints
{
    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/chat/direct", CreateDirectChatCompletion)
            .WithName("CreateDirectChatCompletion")
            .WithSummary("Run a direct (non-RAG) chat completion against the configured model gateway.")
            .Produces<DirectChatResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        api.MapPost("/chat/rag", CreateRagChatCompletion)
            .WithName("CreateRagChatCompletion")
            .WithSummary("Run a retrieval-augmented chat completion using indexed document context.")
            .Produces<RagChatResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        api.MapPost("/chat/agentic", CreateAgenticChatCompletion)
            .WithName("CreateAgenticChatCompletion")
            .WithSummary("Run an agentic chat completion: the model may propose backend tool calls subject to policy.")
            .Produces<AgenticChatResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status502BadGateway);

        return api;
    }

    private static async Task<IResult> CreateDirectChatCompletion(
        DirectChatHttpRequest request,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchAsync<DirectChatCommand, DirectChatResponse>(
            new DirectChatCommand(
                request.Message!,
                request.Model,
                request.Temperature,
                request.MaxOutputTokens,
                request.CorrelationId),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateRagChatCompletion(
        RagChatHttpRequest request,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var documentIds = DocumentIdRequestReader.ReadDocumentIds(request.DocumentIds);

        var result = await dispatcher.DispatchAsync<RagChatCommand, RagChatResponse>(
            new RagChatCommand(
                request.Message!,
                request.Model,
                request.Temperature,
                request.MaxOutputTokens,
                request.TopK,
                request.MinSimilarityScore,
                documentIds,
                request.CorrelationId),
            cancellationToken);

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAgenticChatCompletion(
        AgenticChatHttpRequest request,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchAsync<AgenticChatCommand, AgenticChatResponse>(
            new AgenticChatCommand(
                request.Message!,
                request.Model,
                request.Temperature,
                request.MaxOutputTokens,
                request.CorrelationId,
                request.ApproveRiskyTools ?? false),
            cancellationToken);

        return Results.Ok(result);
    }
}
