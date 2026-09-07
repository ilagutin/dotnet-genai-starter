using GenAIPlatform.Application.Agentic.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Mcp;

internal static class ExternalMcpSetup
{
    public static IServiceCollection AddExternalMcpInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<ExternalMcpOptions>()
            .Bind(configuration.GetSection(ExternalMcpOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ExternalMcpOptions>,
            ExternalMcpOptionsValidator>());

        services.TryAddSingleton<IExternalMcpClientFactory, SdkExternalMcpClientFactory>();
        services.TryAddSingleton<IExternalMcpConnectionPolicy, AlwaysConnectMcpPolicy>();
        services.TryAddSingleton<ExternalMcpConnectionManager>();
        services.TryAddSingleton<IExternalMcpConnectionManager>(
            serviceProvider => serviceProvider.GetRequiredService<ExternalMcpConnectionManager>());
        if (HasEnabledServers(configuration))
        {
            services.AddHostedService(
                serviceProvider => serviceProvider.GetRequiredService<ExternalMcpConnectionManager>());
        }

        services.TryAddEnumerable(ServiceDescriptor.Scoped<IExternalAgentToolSource, ExternalMcpAgentToolSource>());

        return services;
    }
    private static bool HasEnabledServers(IConfiguration configuration)
    {
        return configuration
            .GetSection(ExternalMcpOptions.SectionName)
            .GetSection(nameof(ExternalMcpOptions.Servers))
            .GetChildren()
            .Any(static server => server.GetValue<bool?>(nameof(ExternalMcpServerOptions.Enabled)) ?? true);
    }
}
