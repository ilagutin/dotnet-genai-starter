using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Evaluations.StartRun.Context;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Generation.Prompts.Templates;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Domain.Evaluations.Checks;
using GenAIPlatform.Domain.Observability;

namespace GenAIPlatform.Application.Evaluations.StartRun.Cases;

internal sealed class EvaluationCaseRunner(
    IAiModelClient modelClient,
    IPromptRenderer promptRenderer,
    ModelGatewayRequestPolicy modelGatewayRequestPolicy,
    IAiModelRequestLogger requestLoggingService,
    IEvaluationCostEstimator costEstimator,
    EvaluationCheckRunner checkRunner,
    EvaluationRetrievalContextBuilder contextBuilder,
    EvaluationFailedCaseFactory failedCaseFactory,
    TimeProvider timeProvider)
{
    public async Task<EvaluationCaseResult> RunAsync(
        EvaluationCase evaluationCase,
        ModelGatewayRequestSettings gateway,
        EvaluationRetrievalConfiguration retrievalConfig,
        string tenantId,
        string userId,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        try
        {
            var context = await contextBuilder.BuildAsync(
                evaluationCase,
                gateway,
                retrievalConfig,
                tenantId,
                userId,
                cancellationToken);
            var aiRequest = await CreateAiRequestAsync(
                evaluationCase,
                gateway,
                context,
                cancellationToken);
            var aiResponse = await requestLoggingService.CompleteAndLogAsync(
                modelClient,
                aiRequest,
                context.RetrievalLatency,
                context.Embedding?.InputTokens,
                context.Embedding?.Provider,
                context.Embedding?.Model,
                context.RetrievedDocuments,
                cancellationToken);
            var cost = await costEstimator.EstimateAsync(
                aiResponse,
                context.Embedding?.InputTokens,
                context.Embedding?.Provider,
                context.Embedding?.Model,
                timeProvider.GetUtcNow(),
                CancellationToken.None);
            var checks = checkRunner.Run(
                evaluationCase,
                aiResponse.Content,
                context.Chunks.Count);

            return CreatePassedOrFailedResult(
                evaluationCase,
                aiResponse.Content,
                context,
                cost,
                checks,
                started);
        }
        catch (OperationCanceledException)
        {
            return failedCaseFactory.Create(
                evaluationCase,
                EvaluationCaseErrorCode.Canceled.ToPublicValue(),
                "Evaluation case was canceled.",
                started);
        }
        catch (Exception exception) when (exception is AiModelException or EmbeddingClientException or RagVectorSearchException)
        {
            return failedCaseFactory.Create(
                evaluationCase,
                EvaluationErrorMapper.NormalizeErrorCode(exception),
                "Evaluation case failed before checks completed.",
                started);
        }
    }

    private async Task<AiModelRequest> CreateAiRequestAsync(
        EvaluationCase evaluationCase,
        ModelGatewayRequestSettings gateway,
        EvaluationRetrievalContext context,
        CancellationToken cancellationToken)
    {
        var renderedPrompt = await promptRenderer.RenderActiveAsync(
            EvaluationPrompt.TemplateName,
            new Dictionary<string, string>
            {
                ["question"] = context.Message,
                ["context"] = context.ContextText
            },
            cancellationToken);
        var request = new AiModelRequest(
            $"{gateway.CorrelationId}-{evaluationCase.Id}",
            gateway.Model,
            [
                new AiChatMessage(AiMessageRole.System, renderedPrompt.SystemMessage),
                new AiChatMessage(AiMessageRole.User, renderedPrompt.UserMessage)
            ],
            gateway.Temperature,
            gateway.MaxOutputTokens,
            renderedPrompt.Metadata);
        modelGatewayRequestPolicy.ValidateInputMessages(request.Messages);
        return request;
    }

    private EvaluationCaseResult CreatePassedOrFailedResult(
        EvaluationCase evaluationCase,
        string answer,
        EvaluationRetrievalContext context,
        CostEstimate? cost,
        IReadOnlyList<EvaluationCheckResult> checks,
        long started)
    {
        var passed = checks.All(static check => check.Passed);

        return new EvaluationCaseResult(
            evaluationCase.Id,
            evaluationCase.Name,
            (passed ? EvaluationCaseStatus.Passed : EvaluationCaseStatus.Failed).ToPublicValue(),
            answer,
            context.Chunks.Count,
            context.Chunks.Count > 0,
            timeProvider.GetElapsedTime(started),
            cost?.Amount ?? 0,
            cost?.Currency ?? "USD",
            ErrorCode: null,
            ErrorMessage: null,
            checks);
    }
}
