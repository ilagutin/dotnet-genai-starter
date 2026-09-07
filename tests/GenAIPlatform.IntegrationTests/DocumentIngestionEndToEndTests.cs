using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Generation.Chat;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace GenAIPlatform.IntegrationTests;

[Collection(PostgresRepositoryCollection.CollectionName)]
public sealed class DocumentIngestionEndToEndTests(
    PostgresRepositoryFixture postgres,
    WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [DockerAvailableFact]
    public async Task EndToEndDocumentIngestion_UploadsProcessesAndReturnsIndexedStatus()
    {
        var connectionString = await postgres.GetConnectionStringAsync();
        await EnsureSchemaAsync(connectionString);
        await CleanDatabaseAsync(connectionString);
        var storageRoot = Path.Combine(Path.GetTempPath(), $"genai-e2e-storage-{Guid.NewGuid():n}");

        try
        {
            using var appFactory = factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:GenAIPlatform"] = connectionString,
                        ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform",
                        ["GenAIPlatform:DocumentStorage:RootPath"] = storageRoot,
                        ["GenAIPlatform:Embeddings:Provider"] = "Mock",
                        ["GenAIPlatform:Embeddings:DefaultModel"] = "mock-embedding",
                        ["GenAIPlatform:DocumentIngestion:MaxIndexingJobsPerPoll"] = "1"
                    });
                });
            });
            using var client = appFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("End To End Notes"), "title");
            form.Add(new StringContent("Private"), "accessLevel");
            var fileContent = new ByteArrayContent(
                Encoding.UTF8.GetBytes("# End To End Notes\n\nThis document should be indexed."));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
            form.Add(fileContent, "file", "e2e.md");
            using var uploadRequest = CreateRequest(HttpMethod.Post, "/api/v1/documents", tenantId: "tenant-a");
            uploadRequest.Content = form;

            var uploadResponse = await client.SendAsync(uploadRequest);

            Assert.Equal(HttpStatusCode.Accepted, uploadResponse.StatusCode);
            var upload = await uploadResponse.Content.ReadFromJsonAsync<UploadDocumentResponse>();
            Assert.NotNull(upload);
            Assert.Equal("PendingIndexing", upload.IndexingStatus);

            using (var scope = appFactory.Services.CreateScope())
            {
                var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();
                var processed = await dispatcher.DispatchAsync<ProcessIndexingJobsCommand, ProcessIndexingJobsResponse>(
                    new ProcessIndexingJobsCommand("e2e-worker", MaxJobs: 1, CorrelationId: "e2e-test"),
                    TestContext.Current.CancellationToken);

                Assert.Equal(1, processed.Claimed);
                Assert.Equal(1, processed.Indexed);
                Assert.Equal(0, processed.Failed);
                Assert.Equal(0, processed.Retried);
            }

            using var statusRequest = CreateRequest(
                HttpMethod.Get,
                $"/api/v1/documents/{upload.DocumentId}",
                tenantId: "tenant-a");
            var statusResponse = await client.SendAsync(statusRequest);

            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            var status = await statusResponse.Content.ReadFromJsonAsync<DocumentStatusResponse>();
            Assert.NotNull(status);
            Assert.Equal(upload.DocumentId, status.DocumentId);
            Assert.Equal("Indexed", status.IndexingStatus);
            Assert.Equal("Completed", status.IndexingJobStatus);
            Assert.True(status.ChunkCount > 0);

            using var ragRequest = CreateRequest(
                HttpMethod.Post,
                "/api/v1/chat/rag",
                tenantId: "tenant-a");
            ragRequest.Content = JsonContent.Create(new
            {
                message = "End To End Notes This document should be indexed.",
                topK = 3,
                correlationId = "e2e-rag"
            });
            var ragResponseMessage = await client.SendAsync(ragRequest);

            Assert.Equal(HttpStatusCode.OK, ragResponseMessage.StatusCode);
            var ragResponse = await ragResponseMessage.Content.ReadFromJsonAsync<RagChatResponse>();
            Assert.NotNull(ragResponse);
            Assert.False(ragResponse.NoContext);
            Assert.Equal("mock", ragResponse.Provider);
            Assert.NotEmpty(ragResponse.Citations);
            Assert.Contains(
                ragResponse.Citations,
                citation => citation.DocumentId == upload.DocumentId);

            using var otherTenantRequest = CreateRequest(
                HttpMethod.Get,
                $"/api/v1/documents/{upload.DocumentId}",
                tenantId: "tenant-b");
            var otherTenantResponse = await client.SendAsync(otherTenantRequest);

            Assert.Equal(HttpStatusCode.NotFound, otherTenantResponse.StatusCode);

            using var otherTenantRagRequest = CreateRequest(
                HttpMethod.Post,
                "/api/v1/chat/rag",
                tenantId: "tenant-b");
            otherTenantRagRequest.Content = JsonContent.Create(new
            {
                message = "What should be indexed?",
                correlationId = "e2e-rag-other-tenant"
            });
            var otherTenantRagResponseMessage = await client.SendAsync(otherTenantRagRequest);

            Assert.Equal(HttpStatusCode.OK, otherTenantRagResponseMessage.StatusCode);
            var otherTenantRagResponse =
                await otherTenantRagResponseMessage.Content.ReadFromJsonAsync<RagChatResponse>();
            Assert.NotNull(otherTenantRagResponse);
            Assert.True(otherTenantRagResponse.NoContext);
            Assert.Empty(otherTenantRagResponse.Citations);
        }
        finally
        {
            if (Directory.Exists(storageRoot))
            {
                Directory.Delete(storageRoot, recursive: true);
            }
        }
    }

    private static HttpRequestMessage CreateRequest(
        HttpMethod method,
        string requestUri,
        string tenantId)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", tenantId);
        return request;
    }

    private static async Task CleanDatabaseAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            TRUNCATE TABLE
                genai.document_chunks,
                genai.indexing_jobs,
                genai.documents;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsureSchemaAsync(string connectionString)
    {
        await PostgresSchemaTestHelper.EnsureSchemaAsync(connectionString);
    }
}
