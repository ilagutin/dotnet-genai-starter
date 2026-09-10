namespace GenAIPlatform.Application.Agentic.Tools;

public sealed record ToolExecutionPayloadMetadata(
    int SourceUtf8Bytes,
    int ReturnedUtf8Bytes,
    bool Truncated);
