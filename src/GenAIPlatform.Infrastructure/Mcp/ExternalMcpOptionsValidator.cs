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
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
