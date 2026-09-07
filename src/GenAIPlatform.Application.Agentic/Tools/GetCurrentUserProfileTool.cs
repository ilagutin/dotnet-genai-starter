using System.Text.Json;
using GenAIPlatform.Application.Agentic.Validation;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Domain.Agentic;

namespace GenAIPlatform.Application.Agentic.Tools;

internal sealed class GetCurrentUserProfileTool(IUserContext userContext) : IAgentTool
{
    public AiToolDefinition Definition { get; } = new(
        "GetCurrentUserProfile",
        "Returns the current demo user's id, tenant, roles and groups.",
        "v1",
        Json("""
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """));

    public ToolPolicyMetadata Policy { get; } = ToolPolicyMetadata.Allowed(
        "Read-only demo profile lookup.");

    public ToolValidationResult Validate(JsonElement arguments)
    {
        return arguments.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined
            ? ToolValidationResult.Valid(Json("{}"))
            : ToolValidationResult.Invalid("invalid_arguments", "GetCurrentUserProfile expects an object argument.");
    }

    public Task<ToolExecutionResult> ExecuteAsync(
        JsonElement sanitizedArguments,
        CancellationToken cancellationToken)
    {
        var output = JsonSerializer.SerializeToElement(new
        {
            userId = userContext.UserId,
            tenantId = userContext.TenantId,
            roles = userContext.Roles,
            groups = userContext.Groups
        });

        return Task.FromResult(new ToolExecutionResult(ToolExecutionStatus.Succeeded, output));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
