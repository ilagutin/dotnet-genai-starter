using System.Text.Json;

namespace GenAIPlatform.Application.Core.ModelClients;

/// <summary>
/// Describes a backend-owned tool that may be offered to a model.
/// </summary>
/// <remarks>
/// The definition is backend-owned model-facing metadata, and its input schema is also the runtime
/// validation contract. Executable behavior, risk, approval and audit policy remain owned by backend
/// tool implementations.
/// </remarks>
/// <param name="Name">The stable backend tool name the model may reference in a proposed tool call.</param>
/// <param name="Description">The concise model-facing description of the tool's intended use.</param>
/// <param name="SchemaVersion">The backend schema version for the tool argument contract.</param>
/// <param name="InputSchema">The JSON schema enforced at runtime and supplied to models for argument planning.</param>
public sealed record AiToolDefinition(
    string Name,
    string Description,
    string SchemaVersion,
    JsonElement InputSchema);
