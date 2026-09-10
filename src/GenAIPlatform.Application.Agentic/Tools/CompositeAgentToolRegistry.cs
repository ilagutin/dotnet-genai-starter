namespace GenAIPlatform.Application.Agentic.Tools;

public sealed class CompositeAgentToolRegistry(
    DemoAgentToolRegistry builtInTools,
    IEnumerable<IExternalAgentToolSource> externalToolSources) : IAgentToolRegistry
{
    public IReadOnlyList<IAgentTool> GetAvailableTools()
    {
        var tools = new List<IAgentTool>(builtInTools.GetAvailableTools());

        foreach (var source in externalToolSources)
        {
            tools.AddRange(source.GetAvailableTools());
        }

        return tools;
    }
}
