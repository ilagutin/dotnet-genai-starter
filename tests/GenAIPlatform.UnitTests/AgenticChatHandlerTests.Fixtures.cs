using System.Text.Json;
using GenAIPlatform.Application.Agentic;
using GenAIPlatform.Application.Agentic.Chat;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Domain.Observability;
using GenAIPlatform.Infrastructure.Observability;
using GenAIPlatform.Infrastructure.Observability.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed partial class AgenticChatHandlerTests
{
    private static IApplicationDispatcher CreateHandler(
        IAiModelClient modelClient,
        IToolAuditLogRepository auditRepository,
        AgenticChatOptions? options = null,
        CapturingAiRequestLogRepository? aiLogRepository = null,
        IPricingRepository? pricingRepository = null,
        IAgentToolRegistry? toolRegistry = null,
        TestUserContext? userContext = null,
        TestLogger<AgentToolExecutor>? toolExecutorLogger = null)
    {
        userContext ??= new TestUserContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(new Microsoft.Extensions.Configuration.ConfigurationManager());
        services.AddSingleton<IAiModelClient>(modelClient);
        services.AddSingleton<IToolAuditLogRepository>(auditRepository);
        services.AddSingleton<IUserContext>(userContext);
        services.AddSingleton<IPromptTemplateProvider>(new InMemoryPromptTemplateProvider());
        services.AddSingleton<IAiRequestLogRepository>(
            aiLogRepository ?? new CapturingAiRequestLogRepository());
        services.AddSingleton<IPricingRepository>(pricingRepository ?? new EmptyPricingRepository());
        services.AddSingleton<ILogger<AiModelRequestLoggingService>>(
            NullLogger<AiModelRequestLoggingService>.Instance);
        services.AddSingleton<ILogger<AiRequestLogWriter>>(
            NullLogger<AiRequestLogWriter>.Instance);
        services.AddSingleton<ILogger<AgentToolExecutor>>(
            toolExecutorLogger is null
                ? NullLogger<AgentToolExecutor>.Instance
                : toolExecutorLogger);
        services.AddSingleton(Options.Create(new ModelGatewayOptions()));
        services.AddSingleton(Options.Create(options ?? new AgenticChatOptions()));
        if (toolRegistry is not null)
        {
            services.AddSingleton<IAgentToolRegistry>(toolRegistry);
        }

        return services
            .BuildServiceProvider()
            .GetRequiredService<IApplicationDispatcher>();
    }

    private static AiModelResponse ResponseWithTool(string toolName, string argumentsJson)
    {
        using var arguments = JsonDocument.Parse(argumentsJson);
        return new AiModelResponse(
            "Tool proposed.",
            "mock-chat",
            "mock",
            new AiModelUsage(5, 2, 7),
            "agent-test",
            [new AiToolCall("call-1", toolName, "v1", arguments.RootElement.Clone())]);
    }

    private static AiModelResponse ResponseWithTools(params (string Id, string Name, string ArgumentsJson)[] toolCalls)
    {
        var calls = toolCalls
            .Select(static toolCall =>
            {
                using var arguments = JsonDocument.Parse(toolCall.ArgumentsJson);
                return new AiToolCall(toolCall.Id, toolCall.Name, "v1", arguments.RootElement.Clone());
            })
            .ToArray();

        return new AiModelResponse(
            "Tools proposed.",
            "mock-chat",
            "mock",
            new AiModelUsage(5, 2, 7),
            "agent-test",
            calls);
    }

    private static AiModelResponse ResponseWithInvalidToolArguments(string toolName, string argumentsText)
    {
        return new AiModelResponse(
            "Tool proposed.",
            "mock-chat",
            "mock",
            new AiModelUsage(5, 2, 7),
            "agent-test",
            [new AiToolCall("call-1", toolName, "v1", JsonSerializer.SerializeToElement(argumentsText))]);
    }

    private static JsonElement EmptyArguments()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static PricingRecord CreatePricingRecord(
        string provider,
        string model,
        decimal inputTokenPricePerMillion,
        decimal outputTokenPricePerMillion)
    {
        return new PricingRecord(
            Guid.NewGuid(),
            provider,
            model,
            "USD",
            inputTokenPricePerMillion,
            outputTokenPricePerMillion,
            EmbeddingTokenPricePerMillion: null,
            DateTimeOffset.Parse("2000-01-01T00:00:00Z"),
            EffectiveToUtc: null);
    }

    private sealed class SequenceModelClient(Queue<AiModelResponse> responses) : IAiModelClient
    {
        public SequenceModelClient(IEnumerable<AiModelResponse> responses)
            : this(new Queue<AiModelResponse>(responses))
        {
        }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responses.Count == 0
                ? new AiModelResponse("Done.", request.Model, "mock", new AiModelUsage(1, 1, 2), request.CorrelationId)
                : responses.Dequeue());
        }
    }

    private sealed class DelayingModelClient : IAiModelClient
    {
        public async Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new AiModelResponse(
                "Too late.",
                request.Model,
                "mock",
                new AiModelUsage(1, 1, 2),
                request.CorrelationId);
        }
    }

    private sealed class ReadOnlyMessagesProbeModelClient : IAiModelClient
    {
        private int calls;

        public IReadOnlyList<AiChatMessage>? SecondCallMessages { get; private set; }

        public Task<AiModelResponse> CompleteAsync(
            AiModelRequest request,
            CancellationToken cancellationToken)
        {
            calls++;
            if (calls == 1)
            {
                Assert.IsNotType<List<AiChatMessage>>(request.Messages);
                var collection = Assert.IsAssignableFrom<ICollection<AiChatMessage>>(request.Messages);
                Assert.True(collection.IsReadOnly);
                Assert.Throws<NotSupportedException>(() =>
                    collection.Add(new AiChatMessage(AiMessageRole.User, "external mutation")));

                return Task.FromResult(ResponseWithTool("GetCurrentUserProfile", "{}"));
            }

            SecondCallMessages = request.Messages.ToArray();
            return Task.FromResult(new AiModelResponse(
                "Done after tool.",
                request.Model,
                "mock",
                new AiModelUsage(1, 1, 2),
                request.CorrelationId));
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

    private sealed class CapturingAiRequestLogRepository : IAiRequestLogRepository
    {
        public List<AiRequestLogEntry> Entries { get; } = [];

        public Task AddAsync(AiRequestLogEntry entry, CancellationToken cancellationToken)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
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

    private sealed class InMemoryPricingRepository(IReadOnlyList<PricingRecord> records) : IPricingRepository
    {
        public Task<PricingRecord?> GetEffectivePricingAsync(
            string provider,
            string model,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            var record = records
                .Where(current =>
                    current.Provider == provider &&
                    current.Model == model &&
                    current.EffectiveFromUtc <= usedAtUtc &&
                    (current.EffectiveToUtc is null || current.EffectiveToUtc > usedAtUtc))
                .OrderByDescending(static current => current.EffectiveFromUtc)
                .FirstOrDefault();

            return Task.FromResult(record);
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

    private sealed class StaticToolRegistry(IReadOnlyList<IAgentTool> tools) : IAgentToolRegistry
    {
        public IReadOnlyList<IAgentTool> GetAvailableTools()
        {
            return tools;
        }
    }

    private sealed class ThrowingDemoTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "CreateSupportTicket",
            "Throws during execution for bounded failure coverage.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Creates an idempotent demo support ticket record.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "CreateSupportTicket expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("boom");
        }
    }

    private sealed class CancelingDemoTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "CreateSupportTicket",
            "Cancels during execution for bounded cancellation coverage.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Creates an idempotent demo support ticket record.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "CreateSupportTicket expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            throw new OperationCanceledException();
        }
    }

    private sealed class UnexpectedStatusDemoTool(ToolExecutionStatus status) : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "CreateSupportTicket",
            "Returns a pipeline-only status for contract diagnostics.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Creates an idempotent demo support ticket record.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "CreateSupportTicket expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(
                status,
                JsonSerializer.SerializeToElement(new { ignored = true }),
                "tool_claimed_pipeline_status",
                "Tool attempted to return a pipeline-only status."));
        }
    }

    private sealed class GetCurrentUserProfileTestTool(IUserContext userContext) : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "GetCurrentUserProfile",
            "Returns the current demo user's id.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Read-only demo profile lookup.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "GetCurrentUserProfile expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                JsonSerializer.SerializeToElement(new { userContext.UserId })));
        }
    }

    private sealed class CustomLookupTool : IAgentTool
    {
        public AiToolDefinition Definition { get; } = new(
            "CustomLookup",
            "Test-only lookup tool registered outside the demo registry.",
            "v1",
            EmptyArguments());

        public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
            "Custom lookup is safe for this request.");

        public ToolValidationResult Validate(JsonElement arguments)
        {
            return arguments.ValueKind == JsonValueKind.Object
                ? ToolValidationResult.Valid(EmptyArguments())
                : ToolValidationResult.Invalid("invalid_arguments", "CustomLookup expects an object argument.");
        }

        public Task<ToolExecutionResult> ExecuteAsync(
            JsonElement sanitizedArguments,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new ToolExecutionResult(
                ToolExecutionStatus.Succeeded,
                JsonSerializer.SerializeToElement(new { value = "custom" })));
        }
    }
}
