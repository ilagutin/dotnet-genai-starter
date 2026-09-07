using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Generation.Prompts.Templates;
using GenAIPlatform.Domain.Observability;

namespace GenAIPlatform.Application.Generation.Chat;

internal sealed class RagChatHandler(
    IAiModelClient modelClient,
    RagPromptBuilder promptBuilder,
    IPromptRenderer promptRenderer,
    RagChatNormalizer normalizer,
    ModelGatewayRequestPolicy modelGatewayRequestPolicy,
    IAiModelRequestLogger requestLogger,
    IUserContext userContext,
    RagContextBudgetResolver contextBudgetResolver,
    RagRetrievalPipeline retrievalPipeline,
    RagNoContextResponseFactory noContextResponseFactory)
    : IRequestHandler<RagChatCommand, RagChatResponse>
{
    public async Task<RagChatResponse> HandleAsync(
        RagChatCommand request,
        CancellationToken cancellationToken)
    {
        var validatedRequest = normalizer.Normalize(request);
        var message = validatedRequest.Message;
        var tenantId = userContext.RequireAuthenticatedTenant();
        var userId = userContext.RequireAuthenticatedUser();
        var modelGatewayRequest = modelGatewayRequestPolicy.Resolve(
            request.Model,
            request.Temperature,
            request.MaxOutputTokens,
            request.CorrelationId);
        var contextBudget = await contextBudgetResolver.ResolveAsync(
            message,
            cancellationToken);
        var retrieval = await retrievalPipeline.RetrieveAsync(
            message,
            tenantId,
            userId,
            validatedRequest,
            modelGatewayRequest,
            cancellationToken);

        if (retrieval.Chunks.Count == 0)
        {
            return await LogAndCreateNoContextResponseAsync(
                modelGatewayRequest,
                retrieval);
        }

        var promptContext = promptBuilder.Build(
            retrieval.Chunks,
            contextBudget);
        if (promptContext.Citations.Count == 0)
        {
            return await LogAndCreateNoContextResponseAsync(
                modelGatewayRequest,
                retrieval);
        }

        var renderedPrompt = await RenderPromptAsync(
            message,
            promptContext.ContextText,
            cancellationToken);
        var messages = new[]
        {
            new AiChatMessage(AiMessageRole.System, renderedPrompt.SystemMessage),
            new AiChatMessage(AiMessageRole.User, renderedPrompt.UserMessage)
        };
        modelGatewayRequestPolicy.ValidateInputMessages(messages);

        var aiResponse = await requestLogger.CompleteAndLogAsync(
            modelClient,
            new AiModelRequest(
                modelGatewayRequest.CorrelationId,
                modelGatewayRequest.Model,
                messages,
                modelGatewayRequest.Temperature,
                modelGatewayRequest.MaxOutputTokens,
                renderedPrompt.Metadata),
            retrieval.RetrievalLatency,
            retrieval.Embedding.InputTokens,
            retrieval.Embedding.Provider,
            retrieval.Embedding.Model,
            ToRetrievedDocumentReferences(promptContext),
            cancellationToken);

        return new RagChatResponse(
            aiResponse.Content,
            aiResponse.Model,
            aiResponse.Provider,
            aiResponse.Usage,
            renderedPrompt.Metadata,
            modelGatewayRequest.CorrelationId,
            NoContext: false,
            promptContext.Citations);
    }

    private async Task<RagChatResponse> LogAndCreateNoContextResponseAsync(
        ModelGatewayRequestSettings modelGatewayRequest,
        RagRetrievalResult retrieval)
    {
        await requestLogger.LogSucceededWithoutModelAsync(
            modelGatewayRequest.CorrelationId,
            modelGatewayRequest.Model,
            retrieval.RetrievalLatency,
            retrieval.Embedding.InputTokens,
            retrieval.Embedding.Provider,
            retrieval.Embedding.Model,
            retrieval.RetrievalLatency,
            retrievedDocuments: []);

        return noContextResponseFactory.Create(modelGatewayRequest);
    }

    private async Task<RenderedPrompt> RenderPromptAsync(
        string message,
        string context,
        CancellationToken cancellationToken)
    {
        return await promptRenderer.RenderActiveAsync(
            RagChatPrompt.TemplateName,
            new Dictionary<string, string>
            {
                ["question"] = message,
                ["context"] = context
            },
            cancellationToken);
    }

    private static IReadOnlyList<RetrievedDocumentReference> ToRetrievedDocumentReferences(
        RagPromptContext promptContext)
    {
        return promptContext.Citations
            .Select(static citation => new RetrievedDocumentReference(
                citation.ReferenceId,
                citation.DocumentId,
                citation.ChunkId))
            .ToArray();
    }
}
