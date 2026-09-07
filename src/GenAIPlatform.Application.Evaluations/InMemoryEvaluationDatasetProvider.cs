using System.Text.Json;
using GenAIPlatform.Domain.Evaluations;
using GenAIPlatform.Domain.Exceptions;

namespace GenAIPlatform.Application.Evaluations;

public sealed class InMemoryEvaluationDatasetProvider : IEvaluationDatasetProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EvaluationDataset> GetDatasetAsync(
        string? datasetVersion,
        CancellationToken cancellationToken)
    {
        await using var stream = typeof(InMemoryEvaluationDatasetProvider).Assembly
            .GetManifestResourceStream("GenAIPlatform.Application.Evaluations.Seeds.evaluation-cases.v1.json");
        if (stream is null)
        {
            throw new InvalidOperationException("Embedded evaluation dataset was not found.");
        }

        var dataset = await JsonSerializer.DeserializeAsync<EvaluationDataset>(
            stream,
            JsonOptions,
            cancellationToken);
        if (dataset is null)
        {
            throw new InvalidOperationException("Embedded evaluation dataset could not be read.");
        }

        if (!string.IsNullOrWhiteSpace(datasetVersion) &&
            !string.Equals(dataset.Version, datasetVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset version '{datasetVersion}' was not found.");
        }

        return dataset;
    }
}
