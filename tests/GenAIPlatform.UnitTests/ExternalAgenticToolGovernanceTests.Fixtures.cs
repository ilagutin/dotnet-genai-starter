using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Infrastructure.Mcp;
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed partial class ExternalAgenticToolGovernanceTests
{
    private static ExternalMcpConnectionManager CreateRealManager(IExternalMcpClientFactory factory, double timeout = 30)
    {
        return new ExternalMcpConnectionManager(
            Options.Create(new ExternalMcpOptions
            {
                RefreshInterval = TimeSpan.Zero,
                Servers = [new ExternalMcpServerOptions { Name = "Orders", Command = "fake", ToolCallTimeoutSeconds = timeout }]
            }),
            factory,
            new AlwaysConnectMcpPolicy(),
            NullLogger<ExternalMcpConnectionManager>.Instance);
    }

    private static void AssertUnknownAudit(ToolAuditLogEntry entry, string hash)
    {
        Assert.Equal("mcp_tool_outcome_unknown", entry.ErrorCode);
        Assert.Equal("Failed", entry.ExecutionStatus);
        Assert.Equal("alice", entry.UserId);
        Assert.Equal("tenant-a", entry.TenantId);
        Assert.Equal("external-c2", entry.CorrelationId);
        Assert.Equal("call-1", entry.ToolCallId);
        Assert.Equal(ExternalToolName, entry.ToolName);
        Assert.Equal(hash, entry.SchemaVersion);
        Assert.Equal("Valid", entry.ValidationStatus);
        Assert.Equal("RequiresApproval", entry.PolicyDecision);
        Assert.Equal("SimulatedApproved", entry.ApprovalState);
        AssertMetadataOnly(entry, hasOutput: false);
    }

    private static IApplicationDispatcher CreateDispatcher(
        IAiModelClient modelClient,
        IToolAuditLogRepository auditRepository,
        IExternalAgentToolSource externalSource,
        AgenticChatOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(new ConfigurationManager());
        services.AddSingleton(modelClient);
        services.AddSingleton(auditRepository);
        services.AddSingleton<IExternalAgentToolSource>(externalSource);
        services.AddSingleton<IUserContext>(new TestUserContext());
        services.AddSingleton<IPromptTemplateProvider>(new InMemoryPromptTemplateProvider());
        services.AddSingleton<IAiRequestLogRepository>(new CapturingAiRequestLogRepository());
        services.AddSingleton<IPricingRepository>(new EmptyPricingRepository());
        services.AddSingleton<ILogger<AiModelRequestLoggingService>>(
            NullLogger<AiModelRequestLoggingService>.Instance);
        services.AddSingleton<ILogger<AiRequestLogWriter>>(
            NullLogger<AiRequestLogWriter>.Instance);
        services.AddSingleton(Options.Create(new ModelGatewayOptions()));
        services.AddSingleton(Options.Create(options ?? new AgenticChatOptions()));

        return services
            .BuildServiceProvider()
            .GetRequiredService<IApplicationDispatcher>();
    }

    private static AiModelResponse ToolResponse(string toolName, string argumentsJson, string schemaVersion = "v1")
    {
        return ToolResponse((toolName, argumentsJson, schemaVersion));
    }

    private static AiModelResponse ToolResponse(params (string ToolName, string ArgumentsJson)[] toolCalls)
    {
        return ToolResponse(toolCalls.Select(static toolCall =>
            (toolCall.ToolName, toolCall.ArgumentsJson, SchemaVersion: "v1")).ToArray());
    }

    private static AiModelResponse ToolResponse(params (string ToolName, string ArgumentsJson, string SchemaVersion)[] toolCalls)
    {
        return new AiModelResponse(
            "External tool proposed.",
            "mock-chat",
            "mock",
            new AiModelUsage(5, 2, 7),
            "external-c2",
            toolCalls
                .Select((toolCall, index) => new AiToolCall(
                    $"call-{index + 1}",
                    toolCall.ToolName,
                    toolCall.SchemaVersion,
                    Json(toolCall.ArgumentsJson)))
                .ToArray());
    }

    private static AiModelResponse FinalResponse(string content)
    {
        return new AiModelResponse(
            content,
            "mock-chat",
            "mock",
            new AiModelUsage(3, 2, 5),
            "external-c2");
    }

    private static JsonElement Schema(string propertyName)
    {
        return Json($$"""
        {
          "type": "object",
          "properties": {
            "{{propertyName}}": { "type": "string" }
          },
          "required": [ "{{propertyName}}" ],
          "additionalProperties": false
        }
        """);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertMetadataOnly(ToolAuditLogEntry entry, bool hasOutput)
    {
        Assert.Equal(
            ["contentOmitted", "utf8Bytes"],
            entry.Arguments.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        Assert.Equal(hasOutput, entry.Output is not null);
        Assert.Null(entry.ErrorMessage);
        if (hasOutput)
        {
            Assert.Equal(
                ["contentOmitted", "returnedUtf8Bytes", "sourceUtf8Bytes", "truncated"],
                entry.Output!.Value.EnumerateObject().Select(static property => property.Name).Order().ToArray());
        }
    }

    private static string SnapshotHash(
        string name,
        string description,
        JsonElement inputSchema)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', name, description, inputSchema.GetRawText()));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class FakeExternalMcpServer(Dictionary<string, FakeExternalMcpToolDescriptor> liveTools)
    {
        public FakeExternalAgentToolSource Source { get; private set; } = null!;

        public static FakeExternalMcpServer Connect(IReadOnlyList<FakeExternalMcpToolDescriptor> tools)
        {
            var server = new FakeExternalMcpServer(tools.ToDictionary(
                static tool => tool.Name,
                static tool => tool,
                StringComparer.Ordinal));
            server.Source = new FakeExternalAgentToolSource(server.SnapshotTools());
            return server;
        }

        public void MutateTool(
            string name,
            string description,
            JsonElement inputSchema)
        {
            liveTools[name] = liveTools[name] with
            {
                Description = description,
                InputSchema = inputSchema
            };
        }

        public string CurrentSchemaVersion(string name)
        {
            var tool = liveTools[name];
            return SnapshotHash(tool.Name, tool.Description, tool.InputSchema);
        }

        private IReadOnlyList<SnapshotExternalTool> SnapshotTools()
        {
            return liveTools.Values
                .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
                .Select(tool => new SnapshotExternalTool(
                    tool.Name,
                    tool.Description,
                    SnapshotHash(tool.Name, tool.Description, tool.InputSchema),
                    tool.InputSchema,
                    tool.Policy,
                    this))
                .ToArray();
        }
    }

    private sealed class FakeExternalAgentToolSource(IReadOnlyList<SnapshotExternalTool> tools)
        : IExternalAgentToolSource
    {
        private readonly bool unavailable;

        private FakeExternalAgentToolSource()
            : this([])
        {
            unavailable = true;
        }

        public IReadOnlyList<SnapshotExternalTool> SnapshotTools { get; } = tools;

        public static FakeExternalAgentToolSource Unavailable()
        {
            return new FakeExternalAgentToolSource();
        }

        public IReadOnlyList<IAgentTool> GetAvailableTools()
        {
            return unavailable ? [] : SnapshotTools;
        }
    }

    private sealed record FakeExternalMcpToolDescriptor(
        string Name,
        string Description,
        JsonElement InputSchema,
        ToolPolicyMetadata Policy)
    {
        public static FakeExternalMcpToolDescriptor RequiresApproval(
            string name,
            string description,
            JsonElement inputSchema)
        {
            return new FakeExternalMcpToolDescriptor(
                name,
                description,
                inputSchema,
                ToolPolicyMetadata.ApprovalRequired("External MCP tools require approval by default."));
        }

        public static FakeExternalMcpToolDescriptor Forbidden(
            string name,
            string description,
            JsonElement inputSchema)
        {
            return new FakeExternalMcpToolDescriptor(
                name,
                description,
                inputSchema,
                new ToolPolicyMetadata(
                    ToolRisk.Forbidden,
                    "Forbidden",
                    "The backend policy forbids this external MCP tool.",
                    RequiresApproval: false,
                    MayExecute: false));
        }
    }

    private sealed class SnapshotExternalTool(
        string name,
        string description,
        string snapshotHash,
        JsonElement inputSchema,
        ToolPolicyMetadata policy,
        FakeExternalMcpServer server) : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(name, description, snapshotHash, inputSchema);

        public ToolPolicyMetadata Policy { get; } = policy;

        public ToolAuditContentPolicy AuditContentPolicy => ToolAuditContentPolicy.MetadataOnly;

        public int ValidateCalls { get; private set; }

        public int ExecuteCalls { get; private set; }

        public ToolValidationResult Validate(JsonElement arguments)
        {
            ValidateCalls++;
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("query", out var query) ||
                query.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(query.GetString()))
            {
                return ToolValidationResult.Invalid(
                    "missing_required_argument",
                    "External MCP test tool requires a non-empty query argument.");
            }

            return ToolValidationResult.Valid(JsonSerializer.SerializeToElement(new
            {
                query = query.GetString()
            }));
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            var output = JsonSerializer.SerializeToElement(new
            {
                ok = true,
                query = sanitizedArguments.GetProperty("query").GetString(),
                liveSchemaVersion = server.CurrentSchemaVersion(Definition.Name)
            });
            var outputBytes = Encoding.UTF8.GetByteCount(output.GetRawText());
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                output,
                PayloadMetadata: new ToolExecutionPayloadMetadata(outputBytes, outputBytes, false)));
        }
    }

    private sealed class SequenceModelClient(Queue<AiModelResponse> responses) : IAiModelClient
    {
        public int CallCount { get; private set; }

        public SequenceModelClient(IEnumerable<AiModelResponse> responses)
            : this(new Queue<AiModelResponse>(responses))
        {
        }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(responses.Count == 0
                ? FinalResponse("Done.")
                : responses.Dequeue());
        }
    }

    private sealed class SnapshotProbeModelClient(
        string toolName,
        string expectedSchemaVersion,
        string expectedDescription) : IAiModelClient
    {
        private int calls;

        public bool SnapshotDefinitionWasObserved { get; private set; }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            calls++;
            if (calls == 1)
            {
                var definition = Assert.Single(request.Tools ?? [], tool => tool.Name == toolName);
                Assert.Equal(expectedSchemaVersion, definition.SchemaVersion);
                Assert.Equal(expectedDescription, definition.Description);
                SnapshotDefinitionWasObserved = true;
                return Task.FromResult(ToolResponse(toolName, """{"query":"A-100"}""", "model-mutated-v9"));
            }

            return Task.FromResult(FinalResponse("Rug-pull checked."));
        }
    }

    private sealed class SingleExternalMcpClientFactory(IExternalMcpClient client) : IExternalMcpClientFactory
    {
        public int CreateCount { get; private set; }

        public Task<IExternalMcpClient> CreateAsync(
            ExternalMcpServerOptions server,
            CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromResult(client);
        }
    }

    private sealed class FakeExternalMcpClient(IReadOnlyList<ExternalMcpToolDescriptor> tools) : IExternalMcpClient
    {
        public int CallCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Func<CancellationToken, Task<ExternalMcpToolCallResult>>? CallOverride { get; set; }

        public static FakeExternalMcpClient WithTools(params ExternalMcpToolDescriptor[] tools)
        {
            return new FakeExternalMcpClient(tools);
        }

        public Task<IReadOnlyList<ExternalMcpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(tools);
        }

        public Task<ExternalMcpToolCallResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return CallOverride?.Invoke(cancellationToken) ?? Task.FromResult(new ExternalMcpToolCallResult(
                IsError: false,
                JsonSerializer.SerializeToElement(new { ok = true }),
                ErrorMessage: null));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
    private sealed class CapturingToolAuditLogRepository : IToolAuditLogRepository
    {
        public List<ToolAuditLogEntry> Entries { get; } = [];

        public Func<CancellationToken, Task>? AddOverride { get; init; }

        public async Task AddAsync(ToolAuditLogEntry entry, CancellationToken cancellationToken)
        {
            if (AddOverride is not null)
            {
                await AddOverride(cancellationToken);
            }

            Entries.Add(entry);
        }
    }

    private sealed class CapturingAiRequestLogRepository : IAiRequestLogRepository
    {
        public Task AddAsync(AiRequestLogEntry entry, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyPricingRepository : IPricingRepository
    {
        public Task<PricingRecord?> GetEffectivePricingAsync(
            string provider,
            string model,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PricingRecord?>(null);
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
