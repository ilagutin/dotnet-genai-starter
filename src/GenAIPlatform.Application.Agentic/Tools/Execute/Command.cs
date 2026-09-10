using System.Text.Json;
using GenAIPlatform.Application.Core.Dispatching;

namespace GenAIPlatform.Application.Agentic.Tools.Execute;

public sealed record ExecuteToolCommand(
    string? ToolName = null,
    JsonElement Arguments = default,
    string? SchemaVersion = null)
    : IRequest<ExecuteToolResponse>;
