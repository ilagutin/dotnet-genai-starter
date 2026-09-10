using System.Text.Json;
using System.Text.Json.Serialization;
using GenAIPlatform.Domain.Prompts;

namespace GenAIPlatform.Application.Generation.Prompts.Rendering;

public sealed class InMemoryPromptTemplateProvider : IPromptTemplateProvider
{
    private const string SeedResourcePathMarker = ".Prompts.Seeds.";

    private static readonly Lazy<IReadOnlyDictionary<string, PromptTemplateVersion>> ActiveVersions =
        new(LoadActiveVersions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public Task<PromptTemplateVersion?> GetActiveVersionAsync(
        string templateName,
        CancellationToken cancellationToken)
    {
        ActiveVersions.Value.TryGetValue(templateName, out var version);
        return Task.FromResult(version);
    }

    private static IReadOnlyDictionary<string, PromptTemplateVersion> LoadActiveVersions()
    {
        var assembly = typeof(InMemoryPromptTemplateProvider).Assembly;
        var activeVersions = new Dictionary<string, PromptTemplateVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(static resourceName =>
                         resourceName.Contains(SeedResourcePathMarker, StringComparison.Ordinal) &&
                         resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Prompt seed resource '{resourceName}' could not be opened.");

            var seed = JsonSerializer.Deserialize<PromptTemplateSeed>(stream, JsonOptions)
                ?? throw new InvalidOperationException($"Prompt seed resource '{resourceName}' is empty.");
            var version = CreateVersion(seed, resourceName);

            if (version.Status != PromptTemplateStatus.Active)
            {
                continue;
            }

            if (!activeVersions.TryAdd(version.TemplateName, version))
            {
                throw new InvalidOperationException(
                    $"Multiple active prompt template versions are configured for '{version.TemplateName}'.");
            }
        }

        return activeVersions;
    }

    private static PromptTemplateVersion CreateVersion(PromptTemplateSeed seed, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(seed.TemplateName) ||
            string.IsNullOrWhiteSpace(seed.Version) ||
            string.IsNullOrWhiteSpace(seed.SystemMessage) ||
            string.IsNullOrWhiteSpace(seed.UserMessageTemplate) ||
            seed.Variables is null ||
            seed.Variables.Count == 0)
        {
            throw new InvalidOperationException(
                $"Prompt seed resource '{resourceName}' is missing required template metadata.");
        }

        return PromptTemplateVersion.Create(
            seed.TemplateName.Trim(),
            seed.Version.Trim(),
            seed.Status,
            seed.SystemMessage,
            seed.UserMessageTemplate,
            seed.Variables
                .Where(static variable => !string.IsNullOrWhiteSpace(variable))
                .Select(static variable => variable.Trim())
                .ToArray(),
            seed.CreatedAtUtc,
            seed.Description);
    }
}
