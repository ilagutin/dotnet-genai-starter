using System.Net;
using System.Net.Http.Json;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Knowledge.Retrieval;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenAIPlatform.IntegrationTests;

public sealed partial class DocumentEndpointTests
{
    [Theory]
    [InlineData("omitted")]
    [InlineData("valid")]
    [InlineData("duplicate")]
    public async Task RagChat_MapsDocumentFiltersToRetrievalQuery(string filterShape)
    {
        var documentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        using var ragFactory = CreateRagFactory();
        using var client = ragFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        object body = filterShape switch
        {
            "valid" => (object)new
            {
                message = "Use this document.",
                documentIds = new[] { documentId }
            },
            "duplicate" => (object)new
            {
                message = "Use this document.",
                documentIds = new[] { documentId, documentId }
            },
            _ => new
            {
                message = "Use available documents."
            }
        };

        var response = await client.PostAsJsonAsync("/api/v1/chat/rag", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var searchStore = ragFactory.Services.GetRequiredService<CapturingRagVectorSearchStore>();
        Assert.NotNull(searchStore.Query);
        if (filterShape == "omitted")
        {
            Assert.Empty(searchStore.Query.DocumentIds);
        }
        else
        {
            Assert.Equal([documentId], searchStore.Query.DocumentIds);
        }
    }

    [Fact]
    public async Task RagChat_RejectsEmptyGuidDocumentIdFilterBeforeEmbeddingOrSearch()
    {
        using var ragFactory = CreateRagFactory();
        using var client = ragFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/rag",
            new
            {
                message = "Use this document.",
                documentIds = new[] { Guid.Empty }
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var embeddingClient = ragFactory.Services.GetRequiredService<CapturingEmbeddingClient>();
        var searchStore = ragFactory.Services.GetRequiredService<CapturingRagVectorSearchStore>();
        Assert.Equal(0, embeddingClient.Calls);
        Assert.Null(searchStore.Query);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("null")]
    public async Task RagChat_RejectsExplicitEmptyOrNullDocumentFilterBeforeEmbeddingOrSearch(
        string filterShape)
    {
        using var ragFactory = CreateRagFactory();
        using var client = ragFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        object body = filterShape switch
        {
            "empty" => (object)new
            {
                message = "Use selected documents.",
                documentIds = Array.Empty<Guid>()
            },
            "null" => (object)new
            {
                message = "Use selected documents.",
                documentIds = (Guid[]?)null
            },
            _ => throw new InvalidOperationException($"Unknown filter shape '{filterShape}'.")
        };

        var response = await client.PostAsJsonAsync("/api/v1/chat/rag", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var bodyText = await response.Content.ReadAsStringAsync();
        Assert.Contains("DocumentIds must be omitted or contain at least one id.", bodyText);
        var embeddingClient = ragFactory.Services.GetRequiredService<CapturingEmbeddingClient>();
        var searchStore = ragFactory.Services.GetRequiredService<CapturingRagVectorSearchStore>();
        var modelClient = ragFactory.Services.GetRequiredService<CapturingRagModelClient>();
        Assert.Equal(0, embeddingClient.Calls);
        Assert.Null(searchStore.Query);
        Assert.Equal(0, modelClient.Calls);
    }

    [Fact]
    public async Task RagChat_RejectsQuestionOverEmbeddingLimitBeforeEmbeddingOrSearch()
    {
        using var ragFactory = CreateRagFactory(
            configurationValues: new Dictionary<string, string?>
            {
                ["GenAIPlatform:Embeddings:MaxInputCharacters"] = "12",
                ["GenAIPlatform:DocumentIngestion:ChunkMaxCharacters"] = "10",
                ["GenAIPlatform:DocumentIngestion:ChunkOverlapCharacters"] = "0",
                ["GenAIPlatform:ModelGateway:MaxInputMessageCharacters"] = "100"
            });
        using var client = ragFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/rag",
            new
            {
                message = "This question is longer than twelve characters."
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("RAG message must be 12 characters or fewer.", body);

        var embeddingClient = ragFactory.Services.GetRequiredService<CapturingEmbeddingClient>();
        var searchStore = ragFactory.Services.GetRequiredService<CapturingRagVectorSearchStore>();
        var modelClient = ragFactory.Services.GetRequiredService<CapturingRagModelClient>();
        Assert.Equal(0, embeddingClient.Calls);
        Assert.Null(searchStore.Query);
        Assert.Equal(0, modelClient.Calls);
    }

    [Fact]
    public async Task RagChat_DoesNotExposeRetrievalFailureDetailOrCallModel()
    {
        using var ragFactory = CreateRagFactory(configureServices: services =>
        {
            services.RemoveAll<IRagVectorSearchStore>();
            services.AddSingleton<IRagVectorSearchStore, ThrowingRagVectorSearchStore>();
        });
        using var client = ragFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/rag",
            new
            {
                message = "Trigger retrieval failure."
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("The retrieval store could not complete the request.", body);
        Assert.Contains("retrieval_unavailable", body);
        Assert.DoesNotContain("raw postgres detail", body);

        var embeddingClient = ragFactory.Services.GetRequiredService<CapturingEmbeddingClient>();
        var modelClient = ragFactory.Services.GetRequiredService<CapturingRagModelClient>();
        Assert.Equal(1, embeddingClient.Calls);
        Assert.Equal(0, modelClient.Calls);
    }

    [Fact]
    public async Task RagChat_DoesNotExposeMalformedRetrievalConnectionStringOrCallModel()
    {
        using var ragFactory = CreateRagFactory(
            configurationValues: new Dictionary<string, string?>
            {
                ["ConnectionStrings:GenAIPlatform"] = "Host=localhost;Port=not-a-number;Username=genai;Password=secret",
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            },
            useConfiguredVectorSearchStore: true);
        using var client = ragFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/rag",
            new
            {
                message = "Trigger malformed retrieval configuration."
            });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("The retrieval store could not complete the request.", body);
        Assert.Contains("retrieval_unavailable", body);
        Assert.DoesNotContain("not-a-number", body);
        Assert.DoesNotContain("secret", body);
        Assert.DoesNotContain("genai", body);
        Assert.DoesNotContain("Host=", body);

        var embeddingClient = ragFactory.Services.GetRequiredService<CapturingEmbeddingClient>();
        var modelClient = ragFactory.Services.GetRequiredService<CapturingRagModelClient>();
        Assert.Equal(0, embeddingClient.Calls);
        Assert.Equal(0, modelClient.Calls);
    }

    [Theory]
    [InlineData("authentication_error", "authentication_error")]
    [InlineData("empty_embedding", "empty_embedding")]
    [InlineData("empty_response", "provider_error")]
    [InlineData("raw_embedding_code", "provider_error")]
    public async Task RagChat_DoesNotExposeEmbeddingFailureDetailOrCallRetrievalOrModel(string providerErrorCode, string publicErrorCode)
    {
        using var ragFactory = CreateRagFactory(configureServices: services =>
        {
            services.RemoveAll<IEmbeddingClient>();
            services.AddSingleton<ThrowingEmbeddingClient>(_ => new(providerErrorCode));
            services.AddSingleton<IEmbeddingClient>(
                serviceProvider => serviceProvider.GetRequiredService<ThrowingEmbeddingClient>());
        });
        using var client = ragFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/rag",
            new
            {
                message = "Trigger embedding failure."
            });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("The upstream embedding provider request failed.", body);
        Assert.Contains(publicErrorCode, body);
        Assert.DoesNotContain("raw embedding detail", body);
        Assert.DoesNotContain("sk-test", body);
        Assert.DoesNotContain(publicErrorCode == providerErrorCode ? "raw_embedding_code" : providerErrorCode, body);
        Assert.DoesNotContain("raw-embedding-code", body);

        var embeddingClient = ragFactory.Services.GetRequiredService<ThrowingEmbeddingClient>();
        var searchStore = ragFactory.Services.GetRequiredService<CapturingRagVectorSearchStore>();
        var modelClient = ragFactory.Services.GetRequiredService<CapturingRagModelClient>();
        Assert.Equal(1, embeddingClient.Calls);
        Assert.Null(searchStore.Query);
        Assert.Equal(0, modelClient.Calls);
    }

    [Theory]
    [InlineData("authentication_error", "authentication_error")]
    [InlineData("empty_response", "empty_response")]
    [InlineData("empty_embedding", "provider_error")]
    [InlineData("Authentication_Error", "provider_error")]
    public async Task RagChat_DoesNotExposeModelFailureDetailAfterRetrieval(string providerErrorCode, string publicErrorCode)
    {
        using var ragFactory = CreateRagFactory(configureServices: services =>
        {
            services.RemoveAll<IRagVectorSearchStore>();
            services.AddSingleton<ReturningRagVectorSearchStore>();
            services.AddSingleton<IRagVectorSearchStore>(
                serviceProvider => serviceProvider.GetRequiredService<ReturningRagVectorSearchStore>());
            services.RemoveAll<IAiModelClient>();
            services.AddSingleton<ThrowingRagModelClient>(_ => new(providerErrorCode));
            services.AddSingleton<IAiModelClient>(
                serviceProvider => serviceProvider.GetRequiredService<ThrowingRagModelClient>());
        });
        using var client = ragFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/rag",
            new
            {
                message = "Trigger model failure after retrieval."
            });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("The upstream model provider request failed.", body);
        Assert.Contains(publicErrorCode, body);
        Assert.DoesNotContain("raw model detail", body);
        Assert.DoesNotContain("sk-test", body);
        Assert.DoesNotContain(publicErrorCode == providerErrorCode ? "raw_model_code" : providerErrorCode, body);
        Assert.DoesNotContain("raw-model-code", body);

        var embeddingClient = ragFactory.Services.GetRequiredService<CapturingEmbeddingClient>();
        var searchStore = ragFactory.Services.GetRequiredService<ReturningRagVectorSearchStore>();
        var modelClient = ragFactory.Services.GetRequiredService<ThrowingRagModelClient>();
        Assert.Equal(1, embeddingClient.Calls);
        Assert.Equal(1, searchStore.Calls);
        Assert.Equal(1, modelClient.Calls);
    }

}
