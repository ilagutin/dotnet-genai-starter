namespace GenAIPlatform.Infrastructure.Mcp;

public sealed class ExternalMcpServerOptions
{
    public string Name { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public string Command { get; init; } = string.Empty;

    public List<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public List<string> AllowedTools { get; init; } = [];

    public double ConnectTimeoutSeconds { get; init; } = 10;

    public double ToolCallTimeoutSeconds { get; init; } = 30;
}
