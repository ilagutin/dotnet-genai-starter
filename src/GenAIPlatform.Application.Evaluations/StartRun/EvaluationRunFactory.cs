using System.Text.Json;
using GenAIPlatform.Application.Core.Configuration;
using GenAIPlatform.Application.Evaluations.StartRun.Context;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Generation.Prompts.Templates;
using GenAIPlatform.Domain.Evaluations;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Evaluations.StartRun;

internal sealed class EvaluationRunFactory(
    IPromptRenderer promptRenderer,
    IOptions<ApplicationOptions> applicationOptions,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EvaluationRunResult> CreateAsync(
        EvaluationDataset dataset,
        ModelGatewayRequestSettings gateway,
        EvaluationRetrievalConfiguration retrievalConfig,
        CancellationToken cancellationToken)
    {
        var promptPreview = await promptRenderer.RenderActiveAsync(
            EvaluationPrompt.TemplateName,
            new Dictionary<string, string>
            {
                ["question"] = "preview",
                ["context"] = string.Empty
            },
            cancellationToken);
        var startedAtUtc = timeProvider.GetUtcNow();

        return new EvaluationRunResult(
            Guid.NewGuid(),
            dataset.Version,
            applicationOptions.Value.RunnerVersion,
            promptPreview.Metadata.Version,
            gateway.Model,
            JsonSerializer.Serialize(
                new EvaluationModelSettings(gateway.Temperature, gateway.MaxOutputTokens),
                JsonOptions),
            JsonSerializer.Serialize(
                retrievalConfig,
                JsonOptions),
            EvaluationRunStatus.Running.ToPublicValue(),
            startedAtUtc,
            CompletedAtUtc: null,
            Cases: []);
    }
}
