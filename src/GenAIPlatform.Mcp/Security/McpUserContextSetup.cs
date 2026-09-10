using GenAIPlatform.Application.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Mcp.Security;

public static class McpUserContextSetup
{
    public static IServiceCollection AddMcpUserContext(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<McpIdentityOptions>()
            .Bind(configuration.GetSection(McpIdentityOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<McpIdentityOptions>,
            McpIdentityOptionsValidator>());

        services.AddScoped<McpUserContext>();
        services.AddScoped<IUserContext>(serviceProvider =>
            serviceProvider.GetRequiredService<McpUserContext>());
        services.AddScoped<IBackgroundUserContext>(serviceProvider =>
            serviceProvider.GetRequiredService<McpUserContext>());

        services.AddHostedService<McpIdentityStartupDiagnostics>();

        return services;
    }
}
