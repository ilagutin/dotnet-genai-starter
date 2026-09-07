using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.UnitTests;

public sealed class AgentToolPolicyTests
{
    [Theory]
    [InlineData("GetCurrentUserProfile", "Allowed", false, true)]
    [InlineData("CreateSupportTicket", "Allowed", false, true)]
    [InlineData("DraftEmail", "RequiresApproval", true, true)]
    public void Decide_ClassifiesRegisteredToolRiskFromToolMetadata(
        string toolName,
        string expectedDecision,
        bool requiresApproval,
        bool mayExecute)
    {
        var tool = GetDemoTool(toolName);
        var decision = new ToolPolicy().Decide(tool?.Policy, toolName);

        Assert.Equal(expectedDecision, decision.Decision);
        Assert.Equal(requiresApproval, decision.RequiresApproval);
        Assert.Equal(mayExecute, decision.MayExecute);
    }

    [Fact]
    public void Decide_UsesPolicyMetadataFromRegisteredTool()
    {
        var tool = new MetadataOnlyTestTool();
        var decision = new ToolPolicy().Decide(tool.Policy, tool.Definition.Name);

        Assert.Equal("Allowed", decision.Decision);
        Assert.False(decision.RequiresApproval);
        Assert.True(decision.MayExecute);
        Assert.Equal("Policy metadata from the tool instance.", decision.Reason);
    }

    [Fact]
    public void Definitions_ExposeOpenAiCompatibleObjectSchemas()
    {
        var tools = new DemoAgentToolRegistry(new TestUserContext()).GetAvailableTools();

        foreach (var tool in tools)
        {
            var schema = tool.Definition.InputSchema;
            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            Assert.True(
                schema.TryGetProperty("type", out var type),
                $"{tool.Definition.Name} schema must declare a JSON object type.");
            Assert.Equal("object", type.GetString());
            Assert.True(
                schema.TryGetProperty("properties", out var properties),
                $"{tool.Definition.Name} schema must declare object properties.");
            Assert.Equal(JsonValueKind.Object, properties.ValueKind);
            Assert.True(
                schema.TryGetProperty("additionalProperties", out var additionalProperties),
                $"{tool.Definition.Name} schema must close extra model arguments.");
            Assert.False(additionalProperties.GetBoolean());
        }
    }

    [Theory]
    [InlineData("DeleteDocument")]
    [InlineData("RunSqlQuery")]
    [InlineData("SendEmail")]
    public void Decide_RejectsKnownForbiddenTools(string toolName)
    {
        var decision = new ToolPolicy().Decide(null, toolName);

        Assert.Equal("Forbidden", decision.Decision);
        Assert.False(decision.MayExecute);
    }

    [Theory]
    [InlineData("mcp_admin_runsqlquery")]
    [InlineData("mcp_admin_run_sql_query")]
    [InlineData("mcp_admin_sendemail")]
    [InlineData("mcp_admin_delete_document")]
    public void Decide_RejectsKnownForbiddenToolFragmentsBeforeRegisteredMetadata(string requestedToolName)
    {
        var metadata = ToolPolicyMetadata.ApprovalRequired("External tool would otherwise require approval only.");

        var decision = new ToolPolicy().Decide(metadata, requestedToolName);

        Assert.Equal("Forbidden", decision.Decision);
        Assert.Equal(ToolRisk.Forbidden, decision.Risk);
        Assert.False(decision.RequiresApproval);
        Assert.False(decision.MayExecute);
    }

    [Fact]
    public void Decide_FailsClosedForUnknownTool()
    {
        var decision = new ToolPolicy().Decide(null, "UnregisteredTool");

        Assert.Equal("UnknownTool", decision.Decision);
        Assert.Equal(ToolRisk.Forbidden, decision.Risk);
        Assert.False(decision.MayExecute);
    }

    [Fact]
    public void Validate_FailsClosedWhenRequiredArgumentsAreMissing()
    {
        var ticketTool = GetDemoTool("CreateSupportTicket");

        using var arguments = JsonDocument.Parse("""{"title":"Missing description"}""");
        var result = ticketTool.Validate(arguments.RootElement);

        Assert.False(result.IsValid);
        Assert.Equal("missing_required_argument", result.ErrorCode);
    }

    [Fact]
    public void Validate_SanitizesDraftEmailAsDraftOnly()
    {
        var draftTool = GetDemoTool("DraftEmail");

        using var arguments = JsonDocument.Parse(
            """{"to":"a@example.test","subject":"Hello","body":"Body","ignored":"value"}""");
        var result = draftTool.Validate(arguments.RootElement);

        Assert.True(result.IsValid);
        Assert.Equal("draft", result.SanitizedArguments.GetProperty("mode").GetString());
        Assert.False(result.SanitizedArguments.TryGetProperty("ignored", out _));
    }

    [Fact]
    public async Task Execute_CreateSupportTicketReturnsStableDemoTicketIdForRepeatedProposal()
    {
        var ticketTool = GetDemoTool("CreateSupportTicket");
        using var arguments = JsonDocument.Parse(
            """{"title":"Help","description":"Need help","priority":"normal"}""");
        var validation = ticketTool.Validate(arguments.RootElement);
        Assert.True(validation.IsValid);

        var first = await ticketTool.ExecuteAsync(validation.SanitizedArguments, CancellationToken.None);
        var second = await ticketTool.ExecuteAsync(validation.SanitizedArguments, CancellationToken.None);

        Assert.Equal(
            first.Output.GetProperty("ticketId").GetString(),
            second.Output.GetProperty("ticketId").GetString());
    }

    private static IAgentTool GetDemoTool(string name)
    {
        return new DemoAgentToolRegistry(new TestUserContext())
            .GetAvailableTools()
            .Single(tool => string.Equals(tool.Definition.Name, name, StringComparison.Ordinal));
    }

    private static JsonElement EmptyArguments()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private sealed class MetadataOnlyTestTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "MetadataOnly",
            "Test-only tool used to prove policy metadata comes from the registered instance.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Policy metadata from the tool instance.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return ToolValidationResult.Valid(EmptyArguments());
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                EmptyArguments()));
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
}
