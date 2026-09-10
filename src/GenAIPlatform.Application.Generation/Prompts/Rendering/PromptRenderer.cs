using System.Text.RegularExpressions;
using GenAIPlatform.Domain.Prompts;

namespace GenAIPlatform.Application.Generation.Prompts.Rendering;

public sealed partial class PromptRenderer(IPromptTemplateProvider templateProvider) : IPromptRenderer
{
    public async Task<RenderedPrompt> RenderActiveAsync(
        string templateName,
        IReadOnlyDictionary<string, string> variables,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateName);
        ArgumentNullException.ThrowIfNull(variables);

        var version = await templateProvider.GetActiveVersionAsync(templateName, cancellationToken);
        if (version is null)
        {
            throw new InvalidOperationException(
                $"No active prompt template version is configured for '{templateName}'.");
        }

        foreach (var variableName in version.Variables)
        {
            if (!variables.ContainsKey(variableName))
            {
                throw new ArgumentException(
                    $"Prompt variable '{variableName}' is required for template '{templateName}'.",
                    nameof(variables));
            }
        }

        ValidateTemplatePlaceholders(version);
        var systemMessage = RenderTemplate(version.SystemMessage, variables);
        var userMessage = RenderTemplate(version.UserMessageTemplate, variables);

        var metadata = new PromptMetadata(
            version.TemplateName,
            version.Version,
            version.ContentHash);

        return new RenderedPrompt(systemMessage, userMessage, metadata);
    }

    private static void ValidateTemplatePlaceholders(PromptTemplateVersion version)
    {
        var allowedVariables = version.Variables.ToHashSet(StringComparer.Ordinal);
        var unknownVariables = new[] { version.SystemMessage, version.UserMessageTemplate }
            .SelectMany(template => PromptVariablePattern().Matches(template))
            .Select(static match => match.Groups["name"].Value)
            .Where(variableName => !allowedVariables.Contains(variableName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (unknownVariables.Length > 0)
        {
            throw new InvalidOperationException(
                $"Prompt template '{version.TemplateName}' version '{version.Version}' contains undeclared variable placeholder(s): {string.Join(", ", unknownVariables)}.");
        }
    }

    private static string RenderTemplate(
        string template,
        IReadOnlyDictionary<string, string> variables)
    {
        return PromptVariablePattern().Replace(
            template,
            match =>
            {
                var variableName = match.Groups["name"].Value;
                return variables[variableName];
            });
    }

    [GeneratedRegex(@"\{\{(?<name>[A-Za-z0-9_.:-]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PromptVariablePattern();
}
