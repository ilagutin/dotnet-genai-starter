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
        var tools = descriptors
            .Where(tool => allowedTools.Count == 0 || allowedTools.Contains(tool.Name))
            .Select(tool => BuildToolSnapshot(serverName, server, tool))
            .GroupBy(tool => tool.PrefixedName, StringComparer.Ordinal)
            .Select(group => group.OrderBy(tool => tool.OriginalName, StringComparer.Ordinal).First())
            .OrderBy(tool => tool.PrefixedName, StringComparer.Ordinal)
            .ToArray();

        return new ExternalMcpServerSnapshot(serverName, order, status, tools);
    }

    private static ExternalMcpToolSnapshot BuildToolSnapshot(
        string serverName,
        ExternalMcpServerOptions server,
        ExternalMcpToolDescriptor descriptor)
    {
        var tool = new ExternalMcpToolSnapshot(
            serverName,
            descriptor.Name,
            ExternalMcpNameSanitizer.BuildPrefixedToolName(serverName, descriptor.Name),
            ExternalMcpDescriptionSanitizer.Sanitize(descriptor.Description),
            SnapshotHash: string.Empty,
            ExternalMcpJsonRoundTrip.CloneObjectSchema(descriptor.InputSchema),
            TimeSpan.FromSeconds(server.ToolCallTimeoutSeconds));

        return tool with { SnapshotHash = ExternalMcpSnapshotHasher.Hash(tool) };
    }
}
