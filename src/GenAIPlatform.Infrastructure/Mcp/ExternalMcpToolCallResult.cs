using System.Text.Json;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed record ExternalMcpToolCallResult(
    bool IsError,
    JsonElement Payload,
    string? ErrorMessage,
    string? ErrorCode = null)
{
    public static ExternalMcpToolCallResult Unavailable(
        string message,
        string errorCode = "mcp_server_unavailable")
    {
        return new ExternalMcpToolCallResult(
            IsError: true,
            ExternalMcpJsonRoundTrip.EmptyObject(),
            message,
            errorCode);
    }
}
