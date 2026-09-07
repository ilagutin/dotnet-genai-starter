using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Embedding;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Failure;
using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Lease;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenAIPlatform.Application.Knowledge.Documents;

internal static class DocumentsSetup
{
    public static IServiceCollection AddDocumentsKnowledge(this IServiceCollection services)
    {
        services.TryAddScoped<UploadDocumentNormalizer>();
        services.TryAddScoped<DocumentUploadFactory>();
        services.TryAddScoped<DocumentUploadRollbackCoordinator>();
        services.TryAddScoped<DocumentUploadRollbackInvoker>();
        services.TryAddScoped<DocumentUploadWorkflow>();
        services.TryAddScoped<ITextExtractor, PlainTextDocumentTextExtractor>();
        services.TryAddScoped<ITextChunker, TextChunker>();
        services.TryAddScoped<IndexingJobLeaseCoordinator>();
        services.TryAddScoped<IndexingJobFailurePolicy>();
        services.TryAddScoped<IndexingJobFailureRecorder>();
        services.TryAddScoped<DiscardedEmbeddingObserver>();
        services.TryAddScoped<IndexingEmbeddingRunner>();
        services.TryAddScoped<IndexingChunkEmbeddingWorkflow>();
        services.TryAddScoped<IndexingJobProcessor>();
        services.TryAddScoped<IndexingJobBatchProcessor>();
        services.TryAddScoped<DocumentStorageCleanupRequestProcessor>();
        services.TryAddScoped<IRequestHandler<UploadDocumentCommand, UploadDocumentResponse>, UploadDocumentHandler>();
        services.TryAddScoped<IRequestHandler<GetDocumentStatusQuery, DocumentStatusResponse?>, GetDocumentStatusHandler>();
        services.TryAddScoped<IRequestHandler<ProcessIndexingJobsCommand, ProcessIndexingJobsResponse>, ProcessIndexingJobsHandler>();
        services.TryAddScoped<
            IRequestHandler<ProcessDocumentStorageCleanupCommand, ProcessDocumentStorageCleanupResponse>,
            ProcessDocumentStorageCleanupHandler>();

        return services;
    }
}
