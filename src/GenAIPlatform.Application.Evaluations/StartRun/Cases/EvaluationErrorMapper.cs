using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;

namespace GenAIPlatform.Application.Evaluations.StartRun.Cases;

internal static class EvaluationErrorMapper
{
    public static string NormalizeErrorCode(Exception exception)
    {
        return exception switch
        {
            AiModelException modelException => modelException.ErrorCode ?? "model_provider_error",
            EmbeddingClientException embeddingException => embeddingException.ErrorCode ?? "embedding_provider_error",
            RagVectorSearchException retrievalException => retrievalException.ErrorCode ?? "retrieval_error",
            _ => "evaluation_error"
        };
    }
}
