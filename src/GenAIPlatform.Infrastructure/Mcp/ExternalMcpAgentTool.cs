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
        catch (ExternalMcpCallCanceledException)
        {
            var unknown = ExternalMcpToolCallResult.OutcomeUnknown();
            return new ToolExecutionResult(
                ToolExecutionStatus.Failed,
                unknown.Payload,
                unknown.ErrorCode,
                unknown.ErrorMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ToolExecutionResult(
                ToolExecutionStatus.Failed,
                ExternalMcpJsonRoundTrip.EmptyObject(),
                "mcp_server_unavailable",
                "External MCP server is unavailable.");
        }
        catch (Exception)
        {
            return new ToolExecutionResult(
                ToolExecutionStatus.Failed,
                ExternalMcpJsonRoundTrip.EmptyObject(),
                "mcp_server_unavailable",
                "External MCP server is unavailable.");
        }
    }
}
