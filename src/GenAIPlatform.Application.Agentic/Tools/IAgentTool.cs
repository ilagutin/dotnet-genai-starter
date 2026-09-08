using System.Text.Json;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools;

public interface IAgentTool
{
    AiToolDefinition Definition { get; }

    ToolPolicyMetadata Policy { get; }

    /// <summary>
    /// Applies tool-specific semantic normalization after the declared schema has accepted the payload.
    /// </summary>
    ToolValidationResult Validate(JsonElement arguments);

    Task<ToolExecutionResult> ExecuteAsync(
        JsonElement sanitizedArguments,
        CancellationToken cancellationToken);
}
