using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Core;
using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Generation;
using GenAIPlatform.Application.Knowledge;
using GenAIPlatform.Application.Usage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.IntegrationTests;

internal static class TestApplicationComposition
{
    public static IServiceCollection AddTestApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddApplicationCore(configuration);
        services.AddKnowledgeApplication(configuration);
        services.AddGenerationApplication(configuration);
        services.AddAgenticApplication(configuration);
        services.AddEvaluationsApplication();
        services.AddUsageApplication();

        return services;
    }
}
