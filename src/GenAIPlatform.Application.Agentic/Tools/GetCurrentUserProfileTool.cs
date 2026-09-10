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
        ToolValidationResult.ParseJson("""
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
        return ToolValidationResult.Valid(ToolValidationResult.ParseJson("{}"));
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
}
