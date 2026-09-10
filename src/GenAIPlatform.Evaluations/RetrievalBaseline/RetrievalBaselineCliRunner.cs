using System.Globalization;
using System.Text.Json;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline;

namespace GenAIPlatform.Evaluations.RetrievalBaseline;

/// <summary>
/// Runs the retrieval baseline and writes its report. Console output and the report file
/// carry dataset ids, hashes, settings and metrics only, never question text, document
/// text, embedding vectors or connection strings. A corpus store failure, including a
/// missing or unusable connection string, is reported as the documented failure exit code
/// rather than as an unhandled exception.
/// </summary>
public static class RetrievalBaselineCliRunner
{
    public const int GatesMetExitCode = 0;
    public const int GateFailedExitCode = 1;
    public const int StoreFailedExitCode = 1;

    private static readonly JsonSerializerOptions ReportJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(
        IApplicationDispatcher dispatcher,
        RetrievalBaselineCliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        RetrievalBaselineReport report;
        try
        {
            report = await dispatcher.DispatchAsync<RunRetrievalBaselineCommand, RetrievalBaselineReport>(
                new RunRetrievalBaselineCommand(DatasetVersion: null, options.CodeRevision),
                cancellationToken);
        }
        catch (RetrievalBaselineStoreException exception)
        {
            // The store normalizes persistence failures into a sanitized message, so the
            // message can be shown as is: it names no host, database, user or credential.
            error.WriteLine(exception.Message);
            return StoreFailedExitCode;
        }

        await WriteReportAsync(report, options.OutputPath, cancellationToken);
        WriteSummary(report, options.OutputPath, output);

        return GetExitCode(report);
    }

    public static int GetExitCode(RetrievalBaselineReport report)
    {
        return report.GatesPassed ? GatesMetExitCode : GateFailedExitCode;
    }

    public static async Task WriteReportAsync(
        RetrievalBaselineReport report,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(report, ReportJsonOptions),
            cancellationToken);
    }

    public static void WriteSummary(
        RetrievalBaselineReport report,
        string outputPath,
        TextWriter output)
    {
        output.WriteLine($"Retrieval baseline dataset: {report.DatasetVersion} ({report.DatasetHash[..12]})");
        output.WriteLine($"Revision: {report.CodeRevision}");
        output.WriteLine(
            $"Embeddings: {report.Configuration.EmbeddingProvider}/{report.Configuration.EmbeddingModel} " +
            $"variant={report.Configuration.MockVariant} dimensions={report.Configuration.EmbeddingDimensions}");
        output.WriteLine(
            $"Recall@{report.Configuration.TopK}: {Format(report.Aggregates.RecallAtK)} " +
            $"over {report.Aggregates.RecallEligibleQueryCount} queries");
        output.WriteLine($"MRR: {Format(report.Aggregates.MeanReciprocalRank)}");
        output.WriteLine(
            $"No-match accuracy: {Format(report.Aggregates.NoMatchAccuracy)} " +
            $"over {report.Aggregates.NoMatchQueryCount} queries");

        foreach (var gate in report.Gates)
        {
            output.WriteLine(
                $"Gate {gate.Name}: {Format(gate.Actual)} against {Format(gate.Threshold)} -> {(gate.Met ? "met" : "not met")}");
        }

        output.WriteLine($"Gates: {(report.GatesPassed ? "all met" : "at least one not met")}");
        output.WriteLine($"Report: {Path.GetFullPath(outputPath)}");
    }

    private static string Format(double value)
    {
        return value.ToString("F4", CultureInfo.InvariantCulture);
    }
}
