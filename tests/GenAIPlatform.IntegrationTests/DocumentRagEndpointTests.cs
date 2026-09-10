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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RagChat_FramesOnlyAccessFilteredChunkTextInTheModelPrompt(
        bool callerOwnsThePrivateDocument)
    {
        using var ragFactory = CreateRagFactory(configureServices: services =>
        {
            services.RemoveAll<IRagVectorSearchStore>();
            services.AddSingleton<AccessFilteredRagVectorSearchStore>();
            services.AddSingleton<IRagVectorSearchStore>(
                serviceProvider => serviceProvider.GetRequiredService<AccessFilteredRagVectorSearchStore>());
        });
        using var client = ragFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        var callerUserId = callerOwnsThePrivateDocument
            ? AccessFilteredRagVectorSearchStore.PrivateOwnerUserId
            : AccessFilteredRagVectorSearchStore.OtherUserId;
        client.DefaultRequestHeaders.Add(
            "X-Demo-Tenant-Id",
            AccessFilteredRagVectorSearchStore.TenantId);
        client.DefaultRequestHeaders.Add("X-Demo-User-Id", callerUserId);

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/rag",
            new
            {
                message = "What do the allowed notes say?"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var searchStore = ragFactory.Services.GetRequiredService<AccessFilteredRagVectorSearchStore>();
        Assert.NotNull(searchStore.Query);
        Assert.Equal(AccessFilteredRagVectorSearchStore.TenantId, searchStore.Query.TenantId);
        Assert.Equal(callerUserId, searchStore.Query.UserId);
        var modelClient = ragFactory.Services.GetRequiredService<CapturingRagModelClient>();
        Assert.Equal(1, modelClient.Calls);
        Assert.NotNull(modelClient.Request);
        var userMessage = modelClient.Request.Messages
            .Last(static message => message.Role == AiMessageRole.User)
            .Content;

        Assert.Contains(
            "<source id=\"1\" title=\"Allowed notes\" file=\"allowed.md\">",
            userMessage,
            StringComparison.Ordinal);
        Assert.Contains(
            AccessFilteredRagVectorSearchStore.AllowedText,
            userMessage,
            StringComparison.Ordinal);
        Assert.EndsWith("</source>", userMessage.TrimEnd(), StringComparison.Ordinal);

        if (callerOwnsThePrivateDocument)
        {
            Assert.Contains(
                "<source id=\"2\" title=\"Private notes\" file=\"private.md\">",
                userMessage,
                StringComparison.Ordinal);
            Assert.Contains(
                AccessFilteredRagVectorSearchStore.DeniedText,
                userMessage,
                StringComparison.Ordinal);
            return;
        }

        Assert.DoesNotContain(
            AccessFilteredRagVectorSearchStore.DeniedText,
            userMessage,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Private notes", userMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("<source id=\"2\"", userMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Stands in for the permission-aware store: it holds one tenant-public chunk and one chunk
    /// from a private document owned by <see cref="PrivateOwnerUserId" />, and applies the same
    /// filter the PostgreSQL store applies in SQL (tenant match, then tenant-public access or
    /// document ownership) to the tenant and user carried by the search query. Only chunks the
    /// caller may read are returned, so denied text never reaches prompt construction.
    /// </summary>
    private sealed class AccessFilteredRagVectorSearchStore : IRagVectorSearchStore
    {
        public const string TenantId = "framing-tenant";
        public const string PrivateOwnerUserId = "private-owner";
        public const string OtherUserId = "other-caller";
        public const string AllowedText = "Allowed retrieval evidence for the caller.";

        /// <summary>Text of the private chunk: readable only by <see cref="PrivateOwnerUserId" />.</summary>
        public const string DeniedText = "Denied retrieval evidence for another owner.";

        private static readonly RetrievedDocumentChunk TenantPublicChunk = new(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            DocumentVersion: 1,
            ChunkPosition: 0,
            "Allowed notes",
            "allowed.md",
            AllowedText,
            SimilarityScore: 0.94);

        private static readonly RetrievedDocumentChunk PrivateChunk = new(
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            DocumentVersion: 1,
            ChunkPosition: 0,
            "Private notes",
            "private.md",
            DeniedText,
            SimilarityScore: 0.93);

        public RagVectorSearchQuery? Query { get; private set; }

        public Task CheckReadinessAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            Query = query;
            var readable = new List<RetrievedDocumentChunk>(2);
            if (string.Equals(query.TenantId, TenantId, StringComparison.Ordinal))
            {
                readable.Add(TenantPublicChunk);
                if (string.Equals(query.UserId, PrivateOwnerUserId, StringComparison.Ordinal))
                {
                    readable.Add(PrivateChunk);
                }
            }

            return Task.FromResult<IReadOnlyList<RetrievedDocumentChunk>>(readable);
        }
    }
}
