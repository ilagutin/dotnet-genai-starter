using System.Net;
using System.Net.Http.Json;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Evaluations.StartRun;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Knowledge.Retrieval;
using GenAIPlatform.Application.Usage.GetUsage;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Infrastructure.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenAIPlatform.IntegrationTests;

public sealed partial class ApiV1EndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_ReturnsHealthyStatusUnderApiV1()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.NotNull(body);
        Assert.Equal("Healthy", body.Status);
        Assert.Equal("api", body.Component);
        Assert.Equal("v1", body.ApiVersion);
    }

    [Fact]
    public async Task OpenApi_DevelopmentPublishesDocument()
    {
        using var developmentFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
        });
        using var client = developmentFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"openapi\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"/api/v1/health\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CurrentUser_UsesDemoHeaders()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "local");
        request.Headers.Add("X-Demo-Roles", "developer,admin");
        request.Headers.Add("X-Demo-Groups", "demo,engineering");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(body);
        Assert.True(body.IsAuthenticated);
        Assert.Equal("alice", body.UserId);
        Assert.Equal("local", body.TenantId);
        Assert.Contains("admin", body.Roles);
        Assert.Contains("engineering", body.Groups);
    }

    [Fact]
    public async Task CurrentUser_DeduplicatesDemoRoleAndGroupHeaders()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "local");
        request.Headers.Add("X-Demo-Roles", ["developer,developer", "Developer,admin"]);
        request.Headers.Add("X-Demo-Groups", ["demo,demo", "Demo,engineering"]);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(body);
        Assert.Equal(["developer", "admin"], body.Roles);
        Assert.Equal(["demo", "engineering"], body.Groups);
    }

    [Fact]
    public async Task DemoAuth_DevelopmentWithoutHeadersUsesDemoDefaults()
    {
        using var developmentFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
        });
        using var client = developmentFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/api/v1/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(body);
        Assert.True(body.IsAuthenticated);
        Assert.Equal("demo-user", body.UserId);
        Assert.Equal("demo-tenant", body.TenantId);
        Assert.Contains("developer", body.Roles);
        Assert.Contains("demo", body.Groups);
    }

    [Fact]
    public void ApiComposition_ResolvesOnlyForegroundUserContext()
    {
        using var scope = factory.Services.CreateScope();

        var contexts = scope.ServiceProvider.GetServices<IUserContext>().ToArray();

        var context = Assert.Single(contexts);
        Assert.Equal("DemoHeaderUserContext", context.GetType().Name);
    }

    [Fact]
    public void DemoAuth_ProductionWithoutExplicitOptInFailsFast()
    {
        using var productionFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("GenAIPlatform:DemoAuth:AllowInNonDevelopment", "false");
        });

        var exception = Record.Exception(() =>
            productionFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            }));

        Assert.NotNull(exception);
        Assert.Contains("Demo header auth", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GenAIPlatform:DemoAuth:Enabled", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GenAIPlatform:DemoAuth:AllowInNonDevelopment", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("genai_dev_password", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DemoAuth_ProductionWithExplicitOptInFailsFast()
    {
        using var productionFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("GenAIPlatform:DemoAuth:Enabled", "true");
            builder.UseSetting("GenAIPlatform:DemoAuth:AllowInNonDevelopment", "true");
        });

        var exception = Record.Exception(() =>
            productionFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            }));

        Assert.NotNull(exception);
        Assert.Contains("real IUserContext", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GenAIPlatform:DemoAuth:Enabled", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GenAIPlatform:DemoAuth:AllowInNonDevelopment", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApiComposition_ProductionUsesRegisteredForegroundUserContext()
    {
        using var productionFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(services =>
            {
                services.AddScoped<IUserContext, TestForegroundUserContext>();
            });
        });
        using var client = productionFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/api/v1/users/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(body);
        Assert.True(body.IsAuthenticated);
        Assert.Equal("real-user", body.UserId);
        Assert.Equal("real-tenant", body.TenantId);
        Assert.Contains("operator", body.Roles);
    }

    [Fact]
    public async Task DemoAuth_NonProductionWithExplicitOptInWithoutUserHeaderIsAnonymous()
    {
        using var demoFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Staging");
            builder.UseSetting("GenAIPlatform:DemoAuth:Enabled", "true");
            builder.UseSetting("GenAIPlatform:DemoAuth:AllowInNonDevelopment", "true");
        });
        using var client = demoFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Add("X-Demo-Roles", "developer,admin");
        request.Headers.Add("X-Demo-Groups", "demo,engineering");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(body);
        Assert.False(body.IsAuthenticated);
        Assert.Null(body.UserId);
        Assert.Null(body.TenantId);
        Assert.Empty(body.Roles);
        Assert.Empty(body.Groups);
    }

    [Fact]
    public async Task DemoAuth_NonProductionWithExplicitOptInUsesDemoHeaders()
    {
        using var demoFactory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Staging");
            builder.UseSetting("GenAIPlatform:DemoAuth:Enabled", "true");
            builder.UseSetting("GenAIPlatform:DemoAuth:AllowInNonDevelopment", "true");
        });
        using var client = demoFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/me");
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "local");
        request.Headers.Add("X-Demo-Roles", "developer,admin");
        request.Headers.Add("X-Demo-Groups", "demo,engineering");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CurrentUserResponse>();
        Assert.NotNull(body);
        Assert.True(body.IsAuthenticated);
        Assert.Equal("alice", body.UserId);
        Assert.Equal("local", body.TenantId);
        Assert.Contains("admin", body.Roles);
        Assert.Contains("engineering", body.Groups);
    }

    [Fact]
    public async Task DirectChat_UsesMockModelByDefault()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/direct",
            new
            {
                message = "What is this project?",
                correlationId = "integration-direct-chat"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DirectChatResponse>();
        Assert.NotNull(body);
        Assert.Equal("mock", body.Provider);
        Assert.Equal("mock-chat", body.Model);
        Assert.Equal("integration-direct-chat", body.CorrelationId);
        Assert.Contains("What is this project?", body.Message);
        Assert.Equal("direct-chat", body.Prompt.TemplateName);
        Assert.Equal("v1", body.Prompt.Version);
        Assert.Matches("^[a-f0-9]{64}$", body.Prompt.ContentHash);
    }

    [Fact]
    public async Task DirectChat_DoesNotUseToolCallHeuristics()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/direct",
            new
            {
                message = "Use my profile.",
                correlationId = "integration-direct-no-tools"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<DirectChatResponse>();
        Assert.NotNull(body);
        Assert.Contains("Use my profile.", body.Message);
        Assert.DoesNotContain("tool call", body.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectChat_RejectsUnconfiguredModel()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/direct",
            new
            {
                message = "Use a model that is not configured.",
                model = "unapproved-model"
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DirectChat_RejectsMessageOverConfiguredLimit()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/direct",
            new
            {
                message = new string('x', 8001)
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/chat/direct", true)]
    [InlineData("/api/v1/chat/direct", false)]
    [InlineData("/api/v1/chat/rag", true)]
    [InlineData("/api/v1/chat/rag", false)]
    [InlineData("/api/v1/chat/agentic", true)]
    [InlineData("/api/v1/chat/agentic", false)]
    public async Task ChatEndpoints_RejectMissingOrBlankMessageWithBadRequest(
        string endpoint,
        bool includeMessage)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = includeMessage
                ? JsonContent.Create(new { message = "   " })
                : JsonContent.Create(new { correlationId = "missing-message-test" })
        };
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "tenant-a");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DirectChat_DoesNotExposeProviderErrorDetail()
    {
        using var failingModelFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAiModelClient>();
                services.AddScoped<IAiModelClient, ThrowingModelClient>();
            }));
        using var client = failingModelFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.PostAsJsonAsync(
            "/api/v1/chat/direct",
            new
            {
                message = "Trigger provider failure."
            });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("The upstream model provider request failed.", body);
        Assert.Contains("provider_error", body);
        Assert.DoesNotContain("provider_down", body);
        Assert.DoesNotContain("provider leaked detail", body);
    }

    private sealed record HealthResponse(
        string Status,
        string Component,
        string ApiVersion,
        DateTimeOffset CheckedAtUtc);

    private sealed record CurrentUserResponse(
        bool IsAuthenticated,
        string? UserId,
        string? TenantId,
        IReadOnlyCollection<string> Roles,
        IReadOnlyCollection<string> Groups);

    private sealed record DirectChatResponse(
        string Message,
        string Model,
        string Provider,
        UsageResponse? Usage,
        PromptResponse Prompt,
        string CorrelationId);

    private sealed record UsageResponse(
        int? InputTokens,
        int? OutputTokens,
        int? TotalTokens);

    private sealed record PromptResponse(
        string TemplateName,
        string Version,
        string ContentHash);

    private sealed record UsageSummaryResponse(
        long Requests,
        long InputTokens,
        long OutputTokens,
        long EmbeddingTokens,
        decimal EstimatedCost,
        string Currency);

    private sealed record EvaluationRunResponse(
        Guid RunId,
        string DatasetVersion,
        string Status,
        IReadOnlyList<EvaluationCaseResponse> Cases);

    private sealed record EvaluationCaseResponse(
        string CaseId,
        string Status);

    private sealed record EvaluationSummaryResponse(
        Guid RunId,
        int TotalCases,
        int PassedCases,
        int FailedCaseCount);

    private sealed record AgenticChatResponseBody(
        Guid ConversationId,
        string Status,
        int ToolCalls,
        IReadOnlyList<AgentToolResultResponse> ToolResults);

    private sealed record AgentToolResultResponse(
        string ToolName,
        string PolicyDecision,
        string ExecutionStatus);

    private WebApplicationFactory<Program> CreateAgenticFactoryWithCapturedAudit()
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolAuditLogRepository>();
                services.AddSingleton<CapturingToolAuditLogRepository>();
                services.AddSingleton<IToolAuditLogRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<CapturingToolAuditLogRepository>());
            }));
    }

    private WebApplicationFactory<Program> CreateEvaluationFactory()
    {
        return factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEvaluationDatasetProvider>();
                services.RemoveAll<IEvaluationRunRepository>();
                services.RemoveAll<IRagVectorSearchStore>();
                services.RemoveAll<IAiRequestLogRepository>();
                services.RemoveAll<IPricingRepository>();
                services.AddSingleton<IEvaluationDatasetProvider, SingleCaseEvaluationDatasetProvider>();
                services.AddSingleton<CapturingEvaluationRunRepository>();
                services.AddSingleton<IEvaluationRunRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<CapturingEvaluationRunRepository>());
                services.AddSingleton<IRagVectorSearchStore, EmptyVectorSearchStore>();
                services.AddSingleton<IAiRequestLogRepository, NoopAiRequestLogRepository>();
                services.AddSingleton<IPricingRepository, ZeroPricingRepository>();
            }));
    }

    private sealed class ThrowingModelClient : IAiModelClient
    {
        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            throw new AiModelException(
                "test-provider",
                "provider leaked detail",
                errorCode: "provider_down",
                statusCode: HttpStatusCode.BadGateway);
        }
    }

    private sealed class CapturingUsageRepository : IUsageRepository
    {
        public int Calls { get; private set; }
        public UsageQuery? Query { get; private set; }

        public Task<UsageSummary> GetUsageAsync(
            UsageQuery query,
            CancellationToken cancellationToken)
        {
            Calls++;
            Query = query;
            return Task.FromResult(new UsageSummary(
                Requests: 2,
                InputTokens: 120,
                OutputTokens: 30,
                EmbeddingTokens: 0,
                EstimatedCost: 0.0042m,
                Currency: "USD"));
        }
    }

    private sealed class SingleCaseEvaluationDatasetProvider : IEvaluationDatasetProvider
    {
        public Task<EvaluationDataset> GetDatasetAsync(
            string? datasetVersion,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new EvaluationDataset(
                "api-test-v1",
                [
                    new EvaluationCase(
                        "case-1",
                        "API and CLI shared case",
                        "Answer with the phrase shared behavior.",
                        [new EvaluationCheck("required_phrase", Phrase: "shared behavior")])
                ]));
        }
    }

    private sealed class CapturingEvaluationRunRepository : IEvaluationRunRepository
    {
        public Dictionary<Guid, CapturedEvaluationRun> Runs { get; } = [];

        public Task AddRunAsync(
            EvaluationRunResult run,
            string tenantId,
            string userId,
            CancellationToken cancellationToken)
        {
            Runs[run.RunId] = new CapturedEvaluationRun(run, tenantId, userId);
            return Task.CompletedTask;
        }

        public Task AddCaseResultAsync(Guid runId, EvaluationCaseResult result, CancellationToken cancellationToken)
        {
            var captured = Runs[runId];
            Runs[runId] = captured with
            {
                Run = captured.Run with { Cases = captured.Run.Cases.Concat([result]).ToArray() }
            };
            return Task.CompletedTask;
        }

        public Task CompleteRunAsync(Guid runId, string status, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
        {
            var captured = Runs[runId];
            Runs[runId] = captured with
            {
                Run = captured.Run with { Status = status, CompletedAtUtc = completedAtUtc }
            };
            return Task.CompletedTask;
        }

        public Task<EvaluationRunResult?> GetRunAsync(
            Guid runId,
            string tenantId,
            string userId,
            CancellationToken cancellationToken)
        {
            if (!Runs.TryGetValue(runId, out var captured) ||
                !string.Equals(captured.TenantId, tenantId, StringComparison.Ordinal) ||
                !string.Equals(captured.UserId, userId, StringComparison.Ordinal))
            {
                return Task.FromResult<EvaluationRunResult?>(null);
            }

            return Task.FromResult<EvaluationRunResult?>(captured.Run);
        }

        public Task<EvaluationRunSummary?> GetSummaryAsync(
            Guid runId,
            string tenantId,
            string userId,
            CancellationToken cancellationToken)
        {
            if (!Runs.TryGetValue(runId, out var captured) ||
                !string.Equals(captured.TenantId, tenantId, StringComparison.Ordinal) ||
                !string.Equals(captured.UserId, userId, StringComparison.Ordinal))
            {
                return Task.FromResult<EvaluationRunSummary?>(null);
            }

            var run = captured.Run;
            var passed = run.Cases.Count(static result => result.Status == "Passed");
            var failed = run.Cases.Count - passed;
            return Task.FromResult<EvaluationRunSummary?>(new EvaluationRunSummary(
                runId,
                run.DatasetVersion,
                run.Status,
                run.Cases.Count,
                passed,
                failed,
                RetrievalHitRate: 0,
                AverageLatencyMs: 1,
                AverageCost: 0,
                CostCurrency: "USD",
                FailedCases: []));
        }

        public sealed record CapturedEvaluationRun(
            EvaluationRunResult Run,
            string TenantId,
            string UserId);
    }

    private sealed class EmptyVectorSearchStore : IRagVectorSearchStore
    {
        public Task CheckReadinessAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<RetrievedDocumentChunk>> SearchAsync(
            RagVectorSearchQuery query,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<RetrievedDocumentChunk>>([]);
        }
    }

    private sealed class NoopAiRequestLogRepository : IAiRequestLogRepository
    {
        public Task AddAsync(AiRequestLogEntry entry, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class ZeroPricingRepository : IPricingRepository
    {
        public Task<PricingRecord?> GetEffectivePricingAsync(
            string provider,
            string model,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PricingRecord?>(new PricingRecord(
                Guid.NewGuid(),
                provider,
                model,
                "USD",
                0,
                0,
                0,
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                EffectiveToUtc: null));
        }
    }

    private sealed class CapturingToolAuditLogRepository : IToolAuditLogRepository
    {
        public List<ToolAuditLogEntry> Entries { get; } = [];

        public Task AddAsync(ToolAuditLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class TestForegroundUserContext : IUserContext
    {
        public bool IsAuthenticated => true;

        public string? UserId => "real-user";

        public string? TenantId => "real-tenant";

        public IReadOnlyCollection<string> Roles { get; } = ["operator"];

        public IReadOnlyCollection<string> Groups { get; } = ["foreground"];
    }
}
