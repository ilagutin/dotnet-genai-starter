using GenAIPlatform.Application.Knowledge.Embeddings;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Failure;

internal sealed class IndexingJobFailurePolicy
{
    public bool ShouldRetry(Exception exception, int attempts, int maxAttempts)
    {
        return IsRetryable(exception) &&
               attempts < Math.Max(1, maxAttempts);
    }

    public string ToPublicErrorCode(Exception exception)
    {
        return exception switch
        {
            DocumentValidationException or DocumentTooLargeException => "document_validation",
            EmbeddingClientException embeddingException => embeddingException.ErrorCode ?? "embedding_provider_error",
            OperationCanceledException => "dependency_canceled",
            IOException => "storage_read_error",
            InvalidDataException => "invalid_document_content",
            UnauthorizedAccessException => "storage_access_error",
            _ => "indexing_failed"
        };
    }

    public string ToPublicFailureReason(Exception exception)
    {
        return exception switch
        {
            DocumentValidationException or DocumentTooLargeException => exception.Message,
            EmbeddingClientException embeddingException => ToEmbeddingFailureReason(embeddingException),
            OperationCanceledException => "Document indexing dependency canceled before the worker stopped.",
            IOException => "Document content could not be read while indexing.",
            InvalidDataException => "Document content is invalid.",
            UnauthorizedAccessException => "Document content could not be read while indexing.",
            _ => "Document indexing failed."
        };
    }

    private static bool IsRetryable(Exception exception)
    {
        return exception switch
        {
            DocumentValidationException or DocumentTooLargeException => false,
            EmbeddingClientException embeddingException => embeddingException.ErrorCode is not
                "authentication_error" and not
                "configuration_error" and not
                "empty_embedding" and not
                "invalid_embedding" and not
                "invalid_json" and not
                "invalid_request",
            _ => true
        };
    }

    private static string ToEmbeddingFailureReason(EmbeddingClientException exception)
    {
        return exception.ErrorCode switch
        {
            "authentication_error" => "Embedding provider authentication failed.",
            "configuration_error" => "Embedding provider configuration is invalid.",
            "empty_embedding" => "Embedding provider returned no embedding vector.",
            "invalid_embedding" => "Embedding provider returned an invalid embedding vector.",
            "provider_timeout" or "timeout" => "Embedding provider timed out while indexing the document.",
            "rate_limited" => "Embedding provider rate limited document indexing.",
            _ => "Embedding provider failed while indexing the document."
        };
    }
}
