using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.UnitTests;

public sealed class CompositeAgentToolRegistryTests
{
    [Fact]
    public void GetAvailableTools_WithoutExternalSourcesKeepsBuiltInOrder()
    {
        var registry = new CompositeAgentToolRegistry(
            new DemoAgentToolRegistry(new TestUserContext()),
            []);

        var names = registry.GetAvailableTools().Select(static tool => tool.Definition.Name).ToArray();

        Assert.Equal(
            ["GetCurrentUserProfile", "CreateSupportTicket", "DraftEmail"],
            names);
    }

    [Fact]
    public void GetAvailableTools_AppendsExternalSourcesInRegistrationOrder()
    {
        var registry = new CompositeAgentToolRegistry(
            new DemoAgentToolRegistry(new TestUserContext()),
            [
                new TestExternalSource([new TestTool("mcp_first_a")]),
                new TestExternalSource([new TestTool("mcp_second_a"), new TestTool("mcp_second_b")])
            ]);

        var names = registry.GetAvailableTools().Select(static tool => tool.Definition.Name).ToArray();

        Assert.Equal(
            [
                "GetCurrentUserProfile",
                "CreateSupportTicket",
                "DraftEmail",
                "mcp_first_a",
                "mcp_second_a",
                "mcp_second_b"
            ],
            names);
    }

    private sealed class TestExternalSource(IReadOnlyList<IAgentTool> tools) : IExternalAgentToolSource
    {
        public IReadOnlyList<IAgentTool> GetAvailableTools() => tools;
    }

    private sealed class TestTool(string name) : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(name, "External test tool.", "snapshot", EmptyObject());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.ApprovalRequired("External test tool.");

        public ToolValidationResult Validate(JsonElement arguments) => ToolValidationResult.Valid(EmptyObject());

        public Task<ToolExecutionResult> ExecuteAsync(JsonElement sanitizedArguments, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, EmptyObject()));
        }
    }

    private sealed class TestUserContext : IUserContext
    {
        public bool IsAuthenticated => true;

        public string? UserId => "alice";

        public string? TenantId => "tenant-a";

        public IReadOnlyCollection<string> Roles { get; } = ["developer"];

        public IReadOnlyCollection<string> Groups { get; } = ["demo"];
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
}
