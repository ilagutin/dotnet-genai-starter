using System.Text.Json;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools;

public sealed record ToolExecutionResult(
    ToolExecutionStatus Status,
    JsonElement Output,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    ToolExecutionPayloadMetadata? PayloadMetadata = null);
