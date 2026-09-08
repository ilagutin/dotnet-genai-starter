using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Infrastructure;
using GenAIPlatform.Infrastructure.Documents.Postgres.IndexingJobs;
using GenAIPlatform.Infrastructure.Documents.Postgres.Metadata;
using GenAIPlatform.Infrastructure.ModelGateway.OpenAi;
using GenAIPlatform.Infrastructure.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.IntegrationTests;

public sealed class CompositionRefactorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Infrastructure_ComposesScopedStoresAndTransientModelCollaborators(bool customClock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var clock = new TestClock();
        if (customClock) { services.AddSingleton<TimeProvider>(clock); }
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["GenAIPlatform:ModelGateway:Provider"] = "OpenAiCompatible",
                ["GenAIPlatform:ModelGateway:OpenAiCompatible:ApiKey"] = "test-key"
            }).Build());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        Assert.Same(customClock ? clock : TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Type[] scoped =
        [
            typeof(IDocumentMetadataRepository), typeof(IIndexingJobRepository), typeof(PostgresDocumentStatusReader),
            typeof(PostgresIndexingSchemaReadiness), typeof(PostgresIndexingJobLock), typeof(PostgresIndexingJobClaimStore),
            typeof(PostgresIndexingJobLeaseStore), typeof(PostgresIndexingJobFailureStore), typeof(PostgresIndexingJobCompletionStore),
            typeof(RagVectorSearchQueryValidator), typeof(RagVectorSearchErrorMapper), typeof(PostgresRagReadinessChecker),
            typeof(PostgresRagSearchExecutor), typeof(IRagVectorSearchStore), typeof(IAiModelClient)
        ];
        foreach (var type in scoped)
        {
            Assert.Equal(ServiceLifetime.Scoped, Assert.Single(services, descriptor => descriptor.ServiceType == type).Lifetime);
            var instance = first.ServiceProvider.GetRequiredService(type);
            Assert.Same(instance, first.ServiceProvider.GetRequiredService(type));
            Assert.NotSame(instance, second.ServiceProvider.GetRequiredService(type));
        }
        Type[] transient =
        [
            typeof(OpenAiModelOptionsResolver), typeof(OpenAiModelRequestFactory), typeof(OpenAiModelResponseMapper),
            typeof(OpenAiModelErrorMapper), typeof(OpenAiModelRetryPolicy), typeof(OpenAiModelCompletionExecutor),
            typeof(OpenAiCompatibleModelClient)
        ];
        foreach (var type in transient)
        {
            Assert.Equal(ServiceLifetime.Transient, Assert.Single(services, descriptor => descriptor.ServiceType == type).Lifetime);
            Assert.NotSame(first.ServiceProvider.GetRequiredService(type), first.ServiceProvider.GetRequiredService(type));
        }
        Assert.IsType<PostgresDocumentMetadataStore>(first.ServiceProvider.GetRequiredService<IDocumentMetadataRepository>());
        Assert.IsType<PostgresIndexingJobRepository>(first.ServiceProvider.GetRequiredService<IIndexingJobRepository>());
        Assert.IsType<OpenAiCompatibleModelClient>(first.ServiceProvider.GetRequiredService<IAiModelClient>());
    }

    private sealed class TestClock : TimeProvider;
}
