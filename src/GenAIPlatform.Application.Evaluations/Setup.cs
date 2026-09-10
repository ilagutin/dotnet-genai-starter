using FluentValidation;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;
using GenAIPlatform.Application.Evaluations.StartRun;
using GenAIPlatform.Application.Evaluations.StartRun.Cases;
using GenAIPlatform.Application.Evaluations.StartRun.Context;
using GenAIPlatform.Domain.Evaluations.Checks;
using GenAIPlatform.Domain.Evaluations.Dataset;
using GenAIPlatform.Domain.Evaluations.Retrieval;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenAIPlatform.Application.Evaluations;

public static class Setup
{
    public static IServiceCollection AddEvaluationsApplication(
        this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(Setup).Assembly, includeInternalTypes: true);
        services.TryAddScoped<StartEvaluationRunNormalizer>();
        services.TryAddScoped<EvaluationDatasetValidator>();
        services.TryAddScoped<EvaluationCheckRunner>();
        services.TryAddScoped<EvaluationRunFactory>();
        services.TryAddScoped<EvaluationRetrievalContextBuilder>();
        services.TryAddScoped<EvaluationFailedCaseFactory>();
        services.TryAddScoped<EvaluationCaseRunner>();
        services.TryAddScoped<IEvaluationCostEstimator, NoopEvaluationCostEstimator>();
        services.TryAddScoped<EvaluationRunCompletionCoordinator>();
        services.TryAddScoped<IEvaluationDatasetProvider, InMemoryEvaluationDatasetProvider>();
        services.TryAddScoped<RetrievalBaselineDatasetValidator>();
        services.TryAddScoped<RetrievalBaselineCorpusBuilder>();
        services.TryAddScoped<RetrievalBaselineQueryExecutor>();
        services.TryAddScoped<RetrievalBaselineGateEvaluator>();
        services.TryAddScoped<IRetrievalBaselineDatasetProvider, InMemoryRetrievalBaselineDatasetProvider>();
        services.TryAddScoped<IRequestHandler<RunRetrievalBaselineCommand, RetrievalBaselineReport>, RunRetrievalBaselineHandler>();
        services.TryAddScoped<IRequestHandler<StartEvaluationRunCommand, EvaluationRunResult>, StartEvaluationRunHandler>();
        services.TryAddScoped<IRequestHandler<GetEvaluationRunQuery, EvaluationRunResult?>, GetEvaluationRunHandler>();
        services.TryAddScoped<IRequestHandler<GetEvaluationSummaryQuery, EvaluationRunSummary?>, GetEvaluationSummaryHandler>();

        return services;
    }
}
