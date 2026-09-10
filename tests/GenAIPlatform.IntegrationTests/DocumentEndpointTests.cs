using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Domain.Documents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenAIPlatform.IntegrationTests;

public sealed partial class DocumentEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task UploadDocument_AcceptsTextFileAndReturnsStatus()
    {
        using var uploadFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDocumentStorage>();
                services.RemoveAll<IDocumentMetadataRepository>();
                services.AddSingleton<FakeDocumentRepository>();
                services.AddSingleton<IDocumentMetadataRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<FakeDocumentRepository>());
                services.AddSingleton<IDocumentStorage, FakeDocumentStorage>();
            }));
        using var client = uploadFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Endpoint Notes"), "title");
        form.Add(new StringContent("TenantPublic"), "accessLevel");
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes("# Notes\n\nHello from upload."));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        form.Add(fileContent, "file", "notes.md");

        var response = await client.PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var upload = await response.Content.ReadFromJsonAsync<UploadDocumentResponse>();
        Assert.NotNull(upload);
        Assert.Equal("Endpoint Notes", upload.Title);
        Assert.Equal("notes.md", upload.FileName);
        Assert.Equal("TenantPublic", upload.AccessLevel);
        Assert.Equal("PendingIndexing", upload.IndexingStatus);
        Assert.Equal("Pending", upload.IndexingJobStatus);

        var statusResponse = await client.GetAsync($"/api/v1/documents/{upload.DocumentId}");

        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var status = await statusResponse.Content.ReadFromJsonAsync<DocumentStatusResponse>();
        Assert.NotNull(status);
        Assert.Equal(upload.DocumentId, status.DocumentId);
        Assert.Equal("PendingIndexing", status.IndexingStatus);
        Assert.Equal(upload.IndexingJobId, status.IndexingJobId);
    }

    [Fact]
    public async Task UploadDocument_RejectsUnsupportedFileExtension()
    {
        using var uploadFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDocumentStorage>();
                services.RemoveAll<IDocumentMetadataRepository>();
                services.AddSingleton<IDocumentMetadataRepository, FakeDocumentRepository>();
                services.AddSingleton<IDocumentStorage, FakeDocumentStorage>();
            }));
        using var client = uploadFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("PDF-like content")), "file", "notes.pdf");

        var response = await client.PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UploadDocument_RequiresExplicitFileFormField()
    {
        using var uploadFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDocumentStorage>();
                services.RemoveAll<IDocumentMetadataRepository>();
                services.AddSingleton<FakeDocumentRepository>();
                services.AddSingleton<IDocumentMetadataRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<FakeDocumentRepository>());
                services.AddSingleton<IDocumentStorage, FakeDocumentStorage>();
            }));
        using var client = uploadFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("# Notes")), "upload", "notes.md");

        var response = await client.PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            0,
            uploadFactory.Services.GetRequiredService<FakeDocumentRepository>().CreateDocumentCalls);
    }

    [Fact]
    public async Task UploadDocument_ReturnsPayloadTooLargeWhenFileExceedsConfiguredLimit()
    {
        using var uploadFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GenAIPlatform:DocumentIngestion:MaxUploadBytes"] = "8"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDocumentStorage>();
                services.RemoveAll<IDocumentMetadataRepository>();
                services.AddSingleton<FakeDocumentRepository>();
                services.AddSingleton<IDocumentMetadataRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<FakeDocumentRepository>());
                services.AddSingleton<IDocumentStorage, FakeDocumentStorage>();
            });
        });
        using var client = uploadFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var form = new MultipartFormDataContent();
        form.Add(
            new ByteArrayContent(Encoding.UTF8.GetBytes("# Notes\n\nThis file is larger than eight bytes.")),
            "file",
            "notes.md");

        var response = await client.PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(
            0,
            uploadFactory.Services.GetRequiredService<FakeDocumentRepository>().CreateDocumentCalls);
    }

    [Fact]
    public async Task UploadDocument_ReturnsPayloadTooLargeWhenStorageRejectsStream()
    {
        using var uploadFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDocumentStorage>();
                services.RemoveAll<IDocumentMetadataRepository>();
                services.AddSingleton<FakeDocumentRepository>();
                services.AddSingleton<IDocumentMetadataRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<FakeDocumentRepository>());
                services.AddSingleton<IDocumentStorage, LimitThrowingDocumentStorage>();
            }));
        using var client = uploadFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("# Notes")), "file", "notes.md");

        var response = await client.PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(
            0,
            uploadFactory.Services.GetRequiredService<FakeDocumentRepository>().CreateDocumentCalls);
    }

    [Fact]
    public async Task UploadDocument_AcceptsFileAtConfiguredLimitWithMultipartFields()
    {
        using var uploadFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GenAIPlatform:DocumentIngestion:MaxUploadBytes"] = "8"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDocumentStorage>();
                services.RemoveAll<IDocumentMetadataRepository>();
                services.AddSingleton<FakeDocumentRepository>();
                services.AddSingleton<IDocumentMetadataRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<FakeDocumentRepository>());
                services.AddSingleton<IDocumentStorage, FakeDocumentStorage>();
            });
        });
        using var client = uploadFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Exact Limit"), "title");
        form.Add(new StringContent("Private"), "accessLevel");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("12345678")), "file", "notes.md");

        var response = await client.PostAsync("/api/v1/documents", form);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(
            1,
            uploadFactory.Services.GetRequiredService<FakeDocumentRepository>().CreateDocumentCalls);
    }

    [Theory]
    [InlineData("bad-request", 413, 1, 8, 413)]
    [InlineData("bad-request", 400, 1, 8, 500)]
    [InlineData("invalid-data", 0, 9, 8, 413)]
    [InlineData("invalid-data", 0, 8, 8, 400)]
    public async Task UploadDocument_MapsOnlyTypedMultipartLimitFailures(
        string exceptionKind, int exceptionStatus, int contentLength, long multipartLimit, int expectedStatus)
    {
        const long maxUploadBytes = 23;
        Exception exception = exceptionKind == "bad-request"
            ? new BadHttpRequestException("synthetic parser failure", exceptionStatus)
            : new InvalidDataException("Multipart body length limit was exceeded");
        using var uploadFactory = CreateThrowingFormFactory(exception, multipartLimit, maxUploadBytes);
        using var client = uploadFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using var content = new ByteArrayContent(new byte[contentLength]);
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        var response = await client.PostAsync("/api/v1/documents", content);
        Assert.Equal((HttpStatusCode)expectedStatus, response.StatusCode);
        Assert.Equal(0, uploadFactory.Services.GetRequiredService<FakeDocumentRepository>().CreateDocumentCalls);
        var body = await response.Content.ReadAsStringAsync();
        if (expectedStatus != 500)
        {
            Assert.Contains(expectedStatus == 413 ? "23 bytes or fewer" : "multipart/form-data is invalid", body);
        }
    }

    private WebApplicationFactory<Program> CreateRagFactory(
        IReadOnlyDictionary<string, string?>? configurationValues = null,
        Action<IServiceCollection>? configureServices = null,
        bool useConfiguredVectorSearchStore = false)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            if (configurationValues is not null)
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(configurationValues));
            }

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmbeddingClient>();
                services.RemoveAll<IAiModelClient>();
                if (!useConfiguredVectorSearchStore)
                {
                    services.RemoveAll<IRagVectorSearchStore>();
                }

                services.AddSingleton<CapturingEmbeddingClient>();
                services.AddSingleton<CapturingRagModelClient>();
                if (!useConfiguredVectorSearchStore)
                {
                    services.AddSingleton<CapturingRagVectorSearchStore>();
                    services.AddSingleton<IRagVectorSearchStore>(
                        serviceProvider => serviceProvider.GetRequiredService<CapturingRagVectorSearchStore>());
                }

                services.AddSingleton<IEmbeddingClient>(
                    serviceProvider => serviceProvider.GetRequiredService<CapturingEmbeddingClient>());
                services.AddSingleton<IAiModelClient>(
                    serviceProvider => serviceProvider.GetRequiredService<CapturingRagModelClient>());
                configureServices?.Invoke(services);
            });
        });
    }

    private WebApplicationFactory<Program> CreateThrowingFormFactory(
        Exception exception,
        long multipartLimit,
        long maxUploadBytes)
    {
        return factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GenAIPlatform:DocumentIngestion:MaxUploadBytes"] = maxUploadBytes.ToString()
                }));
            builder.ConfigureTestServices(services =>
            {
                services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = multipartLimit);
                services.AddSingleton<IStartupFilter>(new ThrowingFormFeatureStartupFilter(exception));
                services.RemoveAll<IDocumentStorage>();
                services.RemoveAll<IDocumentMetadataRepository>();
                services.AddSingleton<FakeDocumentRepository>();
                services.AddSingleton<IDocumentMetadataRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<FakeDocumentRepository>());
                services.AddSingleton<IDocumentStorage, FakeDocumentStorage>();
            });
        });
    }

    private sealed record UploadDocumentResponse(
        Guid DocumentId,
        string Title,
        string FileName,
        int Version,
        string AccessLevel,
        string IndexingStatus,
        Guid IndexingJobId,
        string IndexingJobStatus,
        DateTimeOffset CreatedAtUtc);

    private sealed record DocumentStatusResponse(
        Guid DocumentId,
        string Title,
        string FileName,
        int Version,
        string AccessLevel,
        string IndexingStatus,
        Guid? IndexingJobId,
        string? IndexingJobStatus,
        int IndexingAttempts,
        int ChunkCount,
        string? FailureReason,
        DateTimeOffset UpdatedAtUtc);

    private sealed class FakeDocumentRepository : IDocumentMetadataRepository
    {
        private Document? document;
        private IndexingJob? indexingJob;

        public int CreateDocumentCalls { get; private set; }

        public Task CreateDocumentWithJobAsync(
            Document createdDocument,
            IndexingJob createdIndexingJob,
            CancellationToken cancellationToken)
        {
            CreateDocumentCalls++;
            document = createdDocument;
            indexingJob = createdIndexingJob;
            return Task.CompletedTask;
        }

        public Task<bool> DocumentExistsAsync(
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(document?.Id == documentId);

        public Task<Document?> GetDocumentForIndexingAsync(
            Guid documentId,
            CancellationToken cancellationToken) =>
            Task.FromResult(document?.Id == documentId ? document : null);

        public Task<DocumentIndexingStatusSnapshot?> GetDocumentStatusAsync(
            Guid documentId,
            string tenantId,
            string? userId,
            CancellationToken cancellationToken)
        {
            if (document is null ||
                document.Id != documentId ||
                document.TenantId != tenantId ||
                (document.AccessLevel != DocumentAccessLevel.TenantPublic && document.OwnerUserId != userId))
            {
                return Task.FromResult<DocumentIndexingStatusSnapshot?>(null);
            }

            return Task.FromResult<DocumentIndexingStatusSnapshot?>(
                new DocumentIndexingStatusSnapshot(document, indexingJob, ChunkCount: 0));
        }

    }

    private sealed class FakeDocumentStorage : IDocumentStorage
    {
        public async Task<StoredDocument> SaveAsync(
            Guid documentId,
            string fileName,
            Stream content,
            long maxSizeBytes,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            if (maxSizeBytes > 0 && buffer.Length > maxSizeBytes)
            {
                throw new DocumentStorageLimitExceededException(maxSizeBytes);
            }

            return new StoredDocument(
                $"memory://{documentId:n}/{fileName}",
                new string('c', 64),
                buffer.Length);
        }

        public Task CommitAsync(
            StoredDocument document,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<Stream> OpenReadAsync(
            string storagePath,
            CancellationToken cancellationToken)
        {
            Stream stream = new MemoryStream(Encoding.UTF8.GetBytes("stored text"));
            return Task.FromResult(stream);
        }

        public Task DeleteAsync(
            string storagePath,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class LimitThrowingDocumentStorage : IDocumentStorage
    {
        public Task<StoredDocument> SaveAsync(
            Guid documentId,
            string fileName,
            Stream content,
            long maxSizeBytes,
            CancellationToken cancellationToken)
        {
            throw new DocumentStorageLimitExceededException(maxSizeBytes);
        }

        public Task CommitAsync(
            StoredDocument document,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<Stream> OpenReadAsync(
            string storagePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(
            string storagePath,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class CapturingEmbeddingClient : IEmbeddingClient
    {
        public int Calls { get; private set; }

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new EmbeddingResponse(
                [1f, 0f],
                request.Model,
                "fake-embedding-provider",
                InputTokens: 2,
                request.CorrelationId));
        }
    }

    private sealed class CapturingRagVectorSearchStore : IRagVectorSearchStore
    {
        public RagVectorSearchQuery? Query { get; private set; }

        public Task CheckReadinessAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            Query = query;
            return Task.FromResult<IReadOnlyList<RetrievedDocumentChunk>>([]);
        }
    }

    private sealed class ThrowingRagVectorSearchStore : IRagVectorSearchStore
    {
        public Task CheckReadinessAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            throw new RagVectorSearchException(
                "postgres",
                "raw postgres detail",
                errorCode: "retrieval_unavailable");
        }
    }

    private sealed class ReturningRagVectorSearchStore : IRagVectorSearchStore
    {
        public int Calls { get; private set; }

        public Task CheckReadinessAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RetrievedDocumentChunk>>(
            [
                new RetrievedDocumentChunk(
                    Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                    Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                    DocumentVersion: 1,
                    ChunkPosition: 0,
                    "Retrieved notes",
                    "retrieved-notes.md",
                    "Retrieved context for model failure coverage.",
                    SimilarityScore: 0.94)
            ]);
        }
    }

    private sealed class ThrowingEmbeddingClient(string errorCode) : IEmbeddingClient
    {
        public int Calls { get; private set; }

        public Task<EmbeddingResponse> CreateEmbeddingAsync(
            EmbeddingRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new EmbeddingClientException(
                "fake-embedding-provider",
                "raw embedding detail with sk-test secret",
                errorCode: errorCode,
                statusCode: HttpStatusCode.BadGateway,
                providerErrorCode: "raw-embedding-code");
        }
    }

    private sealed class CapturingRagModelClient : IAiModelClient
    {
        public int Calls { get; private set; }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AiModelResponse(
                "unused",
                request.Model,
                "fake",
                Usage: null,
                request.CorrelationId));
        }
    }

    private sealed class ThrowingRagModelClient(string errorCode) : IAiModelClient
    {
        public int Calls { get; private set; }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new AiModelException(
                "fake-model-provider",
                "raw model detail with sk-test secret",
                errorCode: errorCode,
                statusCode: HttpStatusCode.BadGateway,
                providerErrorCode: "raw-model-code");
        }
    }
}
