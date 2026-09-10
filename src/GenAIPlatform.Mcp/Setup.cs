using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Core;
using GenAIPlatform.Application.Generation;
using GenAIPlatform.Application.Knowledge;
using GenAIPlatform.Application.Usage;
using GenAIPlatform.Infrastructure;
using GenAIPlatform.Mcp.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.Mcp;

public static class Setup
{
    public static IServiceCollection AddGenAIPlatformMcp(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddApplicationCore(configuration);
        services.AddKnowledgeApplication(configuration);
        services.AddGenerationApplication(configuration);
        services.AddAgenticApplication(configuration);
        services.AddUsageApplication();
        services.AddInfrastructure(configuration);
        services.AddMcpUserContext(configuration);

        return services;
    }
}
