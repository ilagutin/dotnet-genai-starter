using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Application.Usage.GetUsage;
using GenAIPlatform.Infrastructure.Agentic;
using GenAIPlatform.Infrastructure.Configuration;
using GenAIPlatform.Infrastructure.Documents.Local;
using GenAIPlatform.Infrastructure.Documents.Postgres.Ingestion;
using GenAIPlatform.Infrastructure.Documents.Postgres.StorageCleanup;
using GenAIPlatform.Infrastructure.Embeddings.Mock;
using GenAIPlatform.Infrastructure.Embeddings.OpenAi;
using GenAIPlatform.Infrastructure.Evaluations;
using GenAIPlatform.Infrastructure.Mcp;
using GenAIPlatform.Infrastructure.ModelGateway.Mock;
using GenAIPlatform.Infrastructure.ModelGateway.OpenAi;
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Postgres;
using GenAIPlatform.Infrastructure.Retrieval;
using GenAIPlatform.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure;

public static class Setup
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Raw ServiceCollection-based tests and non-host composition still need IConfiguration
        // for connection-string resolution in PostgreSQL adapters.
        services.TryAddSingleton(configuration);
        services.AddInfrastructureOptions(configuration);
        services.AddModelGatewayAdapters();
        services.AddEmbeddingAdapters();
        services.AddObservabilityInfrastructure(configuration);
        services.AddExternalMcpInfrastructure(configuration);
        services.AddPersistenceAdapters();
        // Infrastructure supplies the background identity used by Worker hosts.
        // API foreground auth must bind IUserContext explicitly.
        services.TryAddScoped<IBackgroundUserContext, SystemUserContext>();

        return services;
    }

    private static IServiceCollection AddInfrastructureOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<PostgresOptions>(configuration.GetSection(PostgresOptions.SectionName));
        services
            .AddOptions<LocalDocumentStorageOptions>()
            .Bind(configuration.GetSection(LocalDocumentStorageOptions.SectionName))
            .Validate(
                static options => IsValidLocalDocumentStorageOptions(options),
                "Local document storage configuration is invalid. Configure GenAIPlatform:DocumentStorage:RootPath as an absolute shared path for API and Worker, or use the local starter-kit fallback from the repository layout when using the default relative path.")
            .ValidateOnStart();
        services
            .AddOptions<ModelGatewayOptions>()
            .Bind(configuration.GetSection(ModelGatewayOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<ModelGatewayOptions>,
            ModelGatewayProviderOptionsValidator>());
        services
            .AddOptions<EmbeddingOptions>()
            .Bind(configuration.GetSection(EmbeddingOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IValidateOptions<EmbeddingOptions>,
            EmbeddingProviderOptionsValidator>());
        services
            .AddOptions<OpenAiCompatibleModelClientOptions>()
            .Bind(configuration.GetSection(OpenAiCompatibleModelClientOptions.SectionName))
            .Validate<IOptions<ModelGatewayOptions>>(
                static (openAiOptions, modelGatewayOptions) =>
                    !ProviderKindParser.IsOpenAiCompatible(modelGatewayOptions.Value.Provider) ||
                    openAiOptions.IsValid(),
                "OpenAI-compatible model gateway configuration is invalid.")
            .ValidateOnStart();
        services
            .AddOptions<OpenAiCompatibleEmbeddingClientOptions>()
            .Bind(configuration.GetSection(OpenAiCompatibleEmbeddingClientOptions.SectionName))
            .Validate<IOptions<EmbeddingOptions>>(
                static (openAiOptions, embeddingOptions) =>
                    !ProviderKindParser.IsOpenAiCompatible(embeddingOptions.Value.Provider) ||
                    openAiOptions.IsValid(),
                "OpenAI-compatible embedding provider configuration is invalid.")
            .ValidateOnStart();

        return services;
    }

    private static IServiceCollection AddModelGatewayAdapters(this IServiceCollection services)
    {
        services.AddHttpClient<OpenAiCompatibleModelClient>();

        services.TryAddScoped<MockAiModelClient>();
        services.TryAddScoped<IAiModelClient>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<ModelGatewayOptions>>()
                .Value;

            if (!ProviderKindParser.TryParse(options.Provider, out var providerKind))
            {
                throw new InvalidOperationException(
                    $"Unsupported model gateway provider '{options.Provider}'.");
            }

            return providerKind switch
            {
                ProviderKind.Mock => serviceProvider.GetRequiredService<MockAiModelClient>(),
                ProviderKind.OpenAiCompatible => serviceProvider.GetRequiredService<OpenAiCompatibleModelClient>(),
                _ => throw new InvalidOperationException(
                    $"Unsupported model gateway provider '{options.Provider}'.")
            };
        });

        return services;
    }

    private static IServiceCollection AddEmbeddingAdapters(this IServiceCollection services)
    {
        services.AddHttpClient<OpenAiCompatibleEmbeddingClient>();

        services.TryAddScoped<MockEmbeddingClient>();
        services.TryAddScoped<IEmbeddingClient>(serviceProvider =>
        {
            var options = serviceProvider
                .GetRequiredService<IOptions<EmbeddingOptions>>()
                .Value;

            if (!ProviderKindParser.TryParse(options.Provider, out var providerKind))
            {
                throw new InvalidOperationException(
                    $"Unsupported embedding provider '{options.Provider}'.");
            }

            return providerKind switch
            {
                ProviderKind.Mock => serviceProvider.GetRequiredService<MockEmbeddingClient>(),
                ProviderKind.OpenAiCompatible => serviceProvider.GetRequiredService<OpenAiCompatibleEmbeddingClient>(),
                _ => throw new InvalidOperationException(
                    $"Unsupported embedding provider '{options.Provider}'.")
            };
        });

        return services;
    }

    private static IServiceCollection AddPersistenceAdapters(this IServiceCollection services)
    {
        services.TryAddSingleton<PostgresDataSourceProvider>();
        services.TryAddScoped<PostgresDocumentIngestionConnectionFactory>();
        services.TryAddScoped<PostgresEvaluationConnectionFactory>();
        services.TryAddScoped<PostgresRagConnectionFactory>();
        services.TryAddScoped<IDocumentStorage, LocalDocumentStorage>();
        services.TryAddScoped<IDocumentIngestionRepository, PostgresDocumentIngestionRepository>();
        services.TryAddScoped<IDocumentStorageCleanupRepository, PostgresDocumentStorageCleanupRepository>();
        services.TryAddScoped<IRagVectorSearchStore, PostgresRagVectorSearchStore>();
        services.TryAddScoped<PostgresObservabilityRepository>();
        services.TryAddScoped<IAiRequestLogRepository>(
            serviceProvider => serviceProvider.GetRequiredService<PostgresObservabilityRepository>());
        services.TryAddScoped<IPricingRepository>(
            serviceProvider => serviceProvider.GetRequiredService<PostgresObservabilityRepository>());
        services.TryAddScoped<IUsageRepository>(
            serviceProvider => serviceProvider.GetRequiredService<PostgresObservabilityRepository>());
        services.TryAddScoped<IEvaluationRunRepository, PostgresEvaluationRunRepository>();
        services.TryAddScoped<IToolAuditLogRepository, PostgresToolAuditLogRepository>();

        return services;
    }

    private static bool IsValidLocalDocumentStorageOptions(LocalDocumentStorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RootPath))
        {
            return false;
        }

        return LocalDocumentStoragePathResolver.CanResolveRootPath(options.RootPath);
    }
}
