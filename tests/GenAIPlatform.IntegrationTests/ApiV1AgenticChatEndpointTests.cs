using System.Net;
using System.Net.Http.Json;
using GenAIPlatform.Application.Agentic.Tools;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenAIPlatform.IntegrationTests;

public sealed partial class ApiV1EndpointTests
{
    [Fact]
    public async Task AgenticChat_ExecutesSafeToolAndReturnsAuditResult()
    {
        using var agenticFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IToolAuditLogRepository>();
                services.AddSingleton<CapturingToolAuditLogRepository>();
                services.AddSingleton<IToolAuditLogRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<CapturingToolAuditLogRepository>());
            }));
        using var client = agenticFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/agentic")
        {
            Content = JsonContent.Create(new
            {
                message = "Use my profile.",
                correlationId = "api-agentic-test"
            })
        };
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "tenant-a");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AgenticChatResponseBody>();
        Assert.NotNull(body);
        Assert.Equal("Succeeded", body.Status);
        Assert.Equal(1, body.ToolCalls);
        var toolResult = Assert.Single(body.ToolResults);
        Assert.Equal("GetCurrentUserProfile", toolResult.ToolName);
        Assert.Equal("Allowed", toolResult.PolicyDecision);
        Assert.Equal("Succeeded", toolResult.ExecutionStatus);

        var audit = agenticFactory.Services.GetRequiredService<CapturingToolAuditLogRepository>();
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("alice", entry.UserId);
        Assert.Equal("tenant-a", entry.TenantId);
        Assert.Equal("api-agentic-test", entry.CorrelationId);
        Assert.Equal("Succeeded", entry.ExecutionStatus);
    }

    [Fact]
    public async Task AgenticChat_DoesNotTriggerToolCallFromIncidentalPromptWrapperWords()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/agentic")
        {
            Content = JsonContent.Create(new
            {
                message = "Summarize wrapper text that mentions profile, ticket, DraftEmail and DeleteDocument.",
                correlationId = "api-agentic-wrapper-words"
            })
        };
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "tenant-a");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AgenticChatResponseBody>();
        Assert.NotNull(body);
        Assert.Equal("Succeeded", body.Status);
        Assert.Equal(0, body.ToolCalls);
        Assert.Empty(body.ToolResults);
    }

    [Theory]
    [InlineData("Create a support ticket.", "Succeeded", "CreateSupportTicket", "Allowed", "Succeeded", false)]
    [InlineData("Draft an email.", "ApprovalRequired", "DraftEmail", "RequiresApproval", "ApprovalRequired", false)]
    [InlineData("Delete a document.", "ToolRejected", "DeleteDocument", "Forbidden", "Rejected", false)]
    public async Task AgenticChat_MockDemoRequestsStillTriggerExpectedTools(
        string message,
        string expectedStatus,
        string expectedToolName,
        string expectedPolicyDecision,
        string expectedExecutionStatus,
        bool approveRiskyTools)
    {
        using var agenticFactory = CreateAgenticFactoryWithCapturedAudit();
        using var client = agenticFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/chat/agentic")
        {
            Content = JsonContent.Create(new
            {
                message,
                correlationId = $"api-agentic-{expectedToolName}",
                approveRiskyTools
            })
        };
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "tenant-a");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AgenticChatResponseBody>();
        Assert.NotNull(body);
        Assert.Equal(expectedStatus, body.Status);
        Assert.Equal(1, body.ToolCalls);
        var toolResult = Assert.Single(body.ToolResults);
        Assert.Equal(expectedToolName, toolResult.ToolName);
        Assert.Equal(expectedPolicyDecision, toolResult.PolicyDecision);
        Assert.Equal(expectedExecutionStatus, toolResult.ExecutionStatus);
    }

}
