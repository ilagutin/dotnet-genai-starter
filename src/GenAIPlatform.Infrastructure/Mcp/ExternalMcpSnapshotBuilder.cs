namespace GenAIPlatform.Infrastructure.Mcp;

internal static class ExternalMcpSnapshotBuilder
{
    public static ExternalMcpServerSnapshot Build(
        ExternalMcpServerOptions server,
        int order,
        IReadOnlyList<ExternalMcpToolDescriptor> descriptors,
        ExternalMcpServerStatus status)
    {
        var serverName = ExternalMcpNameSanitizer.SanitizeSegment(server.Name, "server");
        var allowedTools = server.AllowedTools.ToHashSet(StringComparer.Ordinal);
        var schemalessTools = server.SchemalessTools.ToHashSet(StringComparer.Ordinal);
        var tools = descriptors
            .Where(tool => allowedTools.Count == 0 || allowedTools.Contains(tool.Name))
            .Select(tool => BuildToolSnapshot(serverName, server, tool, schemalessTools))
            .Where(static tool => tool is not null)
            .Select(static tool => tool!)
            .GroupBy(tool => tool.PrefixedName, StringComparer.Ordinal)
            .Select(group => group.OrderBy(tool => tool.OriginalName, StringComparer.Ordinal).First())
            .OrderBy(tool => tool.PrefixedName, StringComparer.Ordinal)
            .ToArray();

        return new ExternalMcpServerSnapshot(serverName, order, status, tools);
    }

    private static ExternalMcpToolSnapshot? BuildToolSnapshot(
        string serverName,
        ExternalMcpServerOptions server,
        ExternalMcpToolDescriptor descriptor,
        IReadOnlySet<string> schemalessTools)
    {
        var inputSchema = ExternalMcpJsonRoundTrip.CloneSchema(descriptor.InputSchema);
        var isSchemaless = inputSchema is null;
        if (isSchemaless && !schemalessTools.Contains(descriptor.Name))
        {
            return null;
        }

        var tool = new ExternalMcpToolSnapshot(
            serverName,
            descriptor.Name,
            ExternalMcpNameSanitizer.BuildPrefixedToolName(serverName, descriptor.Name),
            ExternalMcpDescriptionSanitizer.Sanitize(descriptor.Description),
            SnapshotHash: string.Empty,
            inputSchema ?? ExternalMcpJsonRoundTrip.SchemalessObjectSchema(),
            isSchemaless,
            TimeSpan.FromSeconds(server.ToolCallTimeoutSeconds));

        return tool with { SnapshotHash = ExternalMcpSnapshotHasher.Hash(tool) };
    }
}
