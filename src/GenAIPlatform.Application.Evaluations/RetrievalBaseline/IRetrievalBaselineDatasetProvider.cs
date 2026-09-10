namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

public interface IRetrievalBaselineDatasetProvider
{
    Task<RetrievalBaselineDatasetSource> GetDatasetAsync(
        string? datasetVersion,
        CancellationToken cancellationToken);
}
