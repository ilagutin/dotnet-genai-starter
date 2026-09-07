using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations.StartRun.Context;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Domain.Evaluations.Dataset;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Evaluations.StartRun;

internal sealed class StartEvaluationRunHandler(
    IEvaluationDatasetProvider datasetProvider,
    IEvaluationRunRepository runRepository,
    ModelGatewayRequestPolicy modelGatewayRequestPolicy,
    StartEvaluationRunNormalizer normalizer,
    EvaluationDatasetValidator datasetValidator,
    IOptions<EmbeddingOptions> embeddingOptions,
    EvaluationRunFactory runFactory,
    EvaluationRunCompletionCoordinator completionCoordinator)
    : IRequestHandler<StartEvaluationRunCommand, EvaluationRunResult>
{
    public async Task<EvaluationRunResult> HandleAsync(
        StartEvaluationRunCommand request,
        CancellationToken cancellationToken)
    {
        var validatedRequest = normalizer.Normalize(request);
        var dataset = datasetValidator.Validate(
            await datasetProvider.GetDatasetAsync(
                request.DatasetVersion,
                cancellationToken));
        var gateway = modelGatewayRequestPolicy.Resolve(
            request.Model,
            request.Temperature,
            request.MaxOutputTokens,
            request.CorrelationId);
        var retrievalConfig = new EvaluationRetrievalConfiguration(
            validatedRequest.TopK,
            validatedRequest.MinSimilarityScore,
            embeddingOptions.Value.Provider,
            embeddingOptions.Value.DefaultModel);
        var run = await runFactory.CreateAsync(
            dataset,
            gateway,
            retrievalConfig,
            cancellationToken);

        await runRepository.AddRunAsync(
            run,
            validatedRequest.TenantId,
            validatedRequest.UserId,
            cancellationToken);

        return await completionCoordinator.RunCasesAndCompleteAsync(
            run,
            dataset.Cases,
            gateway,
            retrievalConfig,
            validatedRequest.TenantId,
            validatedRequest.UserId,
            cancellationToken);
    }
}
