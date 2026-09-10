using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Usage.GetUsage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenAIPlatform.Application.Usage;

public static class Setup
{
    public static IServiceCollection AddUsageApplication(
        this IServiceCollection services)
    {
        services.TryAddScoped<UsageQueryScopeResolver>();
        services.TryAddScoped<IRequestHandler<UsageQuery, UsageSummary>, UsageQueryHandler>();

        return services;
    }
}
