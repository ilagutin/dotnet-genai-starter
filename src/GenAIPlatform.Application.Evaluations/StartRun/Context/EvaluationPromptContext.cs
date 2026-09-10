using GenAIPlatform.Domain.Observability;

namespace GenAIPlatform.Application.Evaluations.StartRun.Context;

internal sealed record EvaluationPromptContext(
    string ContextText,
    IReadOnlyList<RetrievedDocumentReference> RetrievedDocuments);
