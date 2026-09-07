namespace GenAIPlatform.Application.Evaluations.StartRun.Context;

public sealed record EvaluationRetrievalConfiguration(
    int TopK,
    double MinSimilarityScore,
    string EmbeddingProvider,
    string EmbeddingModel);
