namespace GenAIPlatform.Application.Agentic.Tools;

public interface IExternalAgentToolSource
{
    IReadOnlyList<IAgentTool> GetAvailableTools();
}
