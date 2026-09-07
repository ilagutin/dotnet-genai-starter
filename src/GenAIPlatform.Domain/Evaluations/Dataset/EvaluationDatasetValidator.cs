using GenAIPlatform.Domain.Exceptions;

namespace GenAIPlatform.Domain.Evaluations.Dataset;

public sealed class EvaluationDatasetValidator
{
    public EvaluationDataset Validate(EvaluationDataset dataset)
    {
        if (string.IsNullOrWhiteSpace(dataset.Version))
        {
            throw new EvaluationValidationException("Evaluation dataset version is required.");
        }

        if (dataset.Cases is not { Count: > 0 })
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{dataset.Version}' must contain at least one case.");
        }

        for (var caseIndex = 0; caseIndex < dataset.Cases.Count; caseIndex++)
        {
            ValidateCase(
                dataset.Version,
                dataset.Cases[caseIndex],
                caseIndex);
        }

        return dataset;
    }

    private static void ValidateCase(
        string datasetVersion,
        EvaluationCase evaluationCase,
        int caseIndex)
    {
        var caseLabel = GetCaseLabel(evaluationCase, caseIndex);
        if (string.IsNullOrWhiteSpace(evaluationCase.Id))
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{datasetVersion}' case {caseLabel} must define a case id.");
        }

        if (string.IsNullOrWhiteSpace(evaluationCase.Name))
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{datasetVersion}' case {caseLabel} must define a case name.");
        }

        if (string.IsNullOrWhiteSpace(evaluationCase.Question))
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{datasetVersion}' case {caseLabel} must define a question.");
        }

        if (evaluationCase.Checks is not { Count: > 0 })
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{datasetVersion}' case {caseLabel} must contain at least one check.");
        }

        var hasFixtureContext = !string.IsNullOrWhiteSpace(evaluationCase.Context);
        for (var checkIndex = 0; checkIndex < evaluationCase.Checks.Count; checkIndex++)
        {
            ValidateCheck(
                datasetVersion,
                caseLabel,
                evaluationCase.Checks[checkIndex],
                checkIndex,
                hasFixtureContext);
        }
    }

    private static void ValidateCheck(
        string datasetVersion,
        string caseLabel,
        EvaluationCheck check,
        int checkIndex,
        bool hasFixtureContext)
    {
        var type = NormalizeType(check.Type);
        if (string.IsNullOrWhiteSpace(type))
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{datasetVersion}' case {caseLabel} check {checkIndex + 1} must define a check type.");
        }

        if ((type == "required_phrase" || type == "forbidden_phrase") &&
            string.IsNullOrWhiteSpace(check.Phrase))
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{datasetVersion}' case {caseLabel} check {checkIndex + 1} ('{type}') requires a non-empty phrase.");
        }

        if (type == "retrieval" && check.MinimumHits <= 0)
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{datasetVersion}' case {caseLabel} check {checkIndex + 1} ('retrieval') requires minimumHits greater than zero.");
        }

        if (type == "retrieval" && hasFixtureContext)
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{datasetVersion}' case {caseLabel} check {checkIndex + 1} ('retrieval') cannot be used with fixture context because retrieval is bypassed.");
        }

        if (type == "citation" &&
            check.CitationReferences is not null &&
            check.CitationReferences.Any(string.IsNullOrWhiteSpace))
        {
            throw new EvaluationValidationException(
                $"Evaluation dataset '{datasetVersion}' case {caseLabel} check {checkIndex + 1} ('citation') contains an empty citation reference.");
        }
    }

    private static string GetCaseLabel(
        EvaluationCase evaluationCase,
        int caseIndex)
    {
        return string.IsNullOrWhiteSpace(evaluationCase.Id)
            ? $"#{caseIndex + 1}"
            : $"'{evaluationCase.Id}'";
    }

    private static string NormalizeType(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
    }
}
