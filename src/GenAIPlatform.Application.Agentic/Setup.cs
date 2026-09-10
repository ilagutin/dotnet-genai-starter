using FluentValidation;
using GenAIPlatform.Application.Agentic.Chat;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Tools.Execute;
using GenAIPlatform.Application.Agentic.Tools.Execution;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Domain.Agentic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Agentic;

public static class Setup
{
    public static IServiceCollection AddAgenticApplication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAgenticOptions(configuration);
        services.AddValidatorsFromAssembly(typeof(Setup).Assembly, includeInternalTypes: true);
        services.TryAddScoped<DemoAgentToolRegistry>();
        services.TryAddScoped<IAgentToolRegistry, CompositeAgentToolRegistry>();
        services.TryAddScoped<ToolPolicy>();
        services.TryAddScoped<AgenticPromptBuilder>();
        services.TryAddScoped<AgentToolAuditLogWriter>();
        services.TryAddScoped<AgentToolAuditWriter>();
        services.TryAddSingleton<AgentToolArgumentValidator>();
        services.TryAddScoped<GovernedAgentToolExecutor>();
        services.TryAddScoped<AgentToolExecutor>();
        services.TryAddScoped<AgenticToolCallProcessor>();
        services.TryAddScoped<IAgenticCostEstimator, NoopAgenticCostEstimator>();
        services.TryAddScoped<AgenticBudgetGuard>();
        services.TryAddScoped<AgenticChatLoopRunner>();
        services.TryAddScoped<IRequestHandler<AgenticChatCommand, AgenticChatResponse>, AgenticChatHandler>();
        services.TryAddScoped<IRequestHandler<ExecuteToolCommand, ExecuteToolResponse>, ExecuteToolHandler>();

        return services;
    }

    private static IServiceCollection AddAgenticOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<AgenticChatOptions>()
            .Bind(configuration.GetSection(AgenticChatOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<AgenticChatOptions>,
            AgenticChatOptionsValidator>());

        return services;
    }
}
