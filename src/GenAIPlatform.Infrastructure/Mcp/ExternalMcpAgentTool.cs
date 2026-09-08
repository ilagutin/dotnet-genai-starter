using System.Text.Json;
using GenAIPlatform.Application.Agentic.Tools;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed class ExternalMcpAgentTool(
    IExternalMcpConnectionManager connectionManager,
    ExternalMcpToolSnapshot snapshot) : IAgentTool
{
    public AiToolDefinition Definition { get; } = new(
        snapshot.PrefixedName,
        snapshot.Description,
        snapshot.SnapshotHash,
        snapshot.InputSchema.Clone());

    public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.ApprovalRequired(
        "External MCP tools require backend approval before execution.");

    public ToolAuditContentPolicy AuditContentPolicy => ToolAuditContentPolicy.MetadataOnly;

    public ToolValidationResult Validate(JsonElement arguments)
    {
        return ToolValidationResult.Valid(arguments.Clone());
    }

    public async Task<ToolExecutionResult> ExecuteAsync(
        JsonElement sanitizedArguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var arguments = ExternalMcpJsonRoundTrip.ToSdkArguments(sanitizedArguments);
            var result = await connectionManager.CallToolAsync(
                snapshot,
                arguments,
                cancellationToken);

            return result.IsError
                ? new ToolExecutionResult(
                    ToolExecutionStatus.Failed,
                    result.Payload,
                    result.ErrorCode ?? "mcp_tool_error",
                    result.ErrorMessage ?? "External MCP tool returned an error.",
                    result.PayloadMetadata)
                : new ToolExecutionResult(
                    ToolExecutionStatus.Succeeded,
                    result.Payload,
                    PayloadMetadata: result.PayloadMetadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ToolExecutionResult(
                ToolExecutionStatus.Failed,
                ExternalMcpJsonRoundTrip.EmptyObject(),
                "mcp_tool_canceled",
                "External MCP tool execution was canceled.");
        }
        catch (OperationCanceledException)
        {
            return new ToolExecutionResult(
                ToolExecutionStatus.Failed,
                ExternalMcpJsonRoundTrip.EmptyObject(),
                "mcp_tool_timeout",
                "External MCP tool execution timed out.");
        }
        catch (Exception)
        {
            return new ToolExecutionResult(
                ToolExecutionStatus.Failed,
                ExternalMcpJsonRoundTrip.EmptyObject(),
                "mcp_tool_failed",
                "External MCP tool execution failed.");
        }
    }
}
