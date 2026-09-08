using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Mcp;

internal sealed class ExternalMcpOptionsValidator : IValidateOptions<ExternalMcpOptions>
{
    public ValidateOptionsResult Validate(string? name, ExternalMcpOptions options)
    {
        var failures = new List<string>();
        var serverNames = new HashSet<string>(StringComparer.Ordinal);

        if (options.MaxParallelConnects < 1)
        {
            failures.Add("External MCP MaxParallelConnects must be at least 1.");
        }

        if (options.RefreshInterval < TimeSpan.Zero)
        {
            failures.Add("External MCP RefreshInterval cannot be negative.");
        }

        foreach (var server in options.Servers.Where(static server => server.Enabled))
        {
            if (string.IsNullOrWhiteSpace(server.Name))
            {
                failures.Add("External MCP server name is required.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(server.Command))
            {
                failures.Add($"External MCP server '{server.Name}' command is required.");
            }

            var sanitizedName = ExternalMcpNameSanitizer.SanitizeSegment(server.Name, "server");
            if (!serverNames.Add(sanitizedName))
            {
                failures.Add($"External MCP server name '{server.Name}' collides after sanitization.");
            }

            if (server.ConnectTimeoutSeconds <= 0)
            {
                failures.Add($"External MCP server '{server.Name}' connect timeout must be positive.");
            }

            if (server.ToolCallTimeoutSeconds <= 0)
            {
                failures.Add($"External MCP server '{server.Name}' tool call timeout must be positive.");
            }

            ValidateSchemalessTools(server, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateSchemalessTools(
        ExternalMcpServerOptions server,
        ICollection<string> failures)
    {
        var allowedTools = server.AllowedTools.ToHashSet(StringComparer.Ordinal);
        var schemalessTools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolName in server.SchemalessTools)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                failures.Add($"External MCP server '{server.Name}' schemaless tool names cannot be blank.");
                continue;
            }

            if (!schemalessTools.Add(toolName))
            {
                failures.Add($"External MCP server '{server.Name}' schemaless tool '{toolName}' is duplicated.");
            }

            if (allowedTools.Count > 0 && !allowedTools.Contains(toolName))
            {
                failures.Add($"External MCP server '{server.Name}' schemaless tool '{toolName}' must be in AllowedTools.");
            }
        }
    }
}
