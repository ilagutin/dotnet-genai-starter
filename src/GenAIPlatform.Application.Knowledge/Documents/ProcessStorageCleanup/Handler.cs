using GenAIPlatform.Application.Core.Dispatching;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents;

internal sealed class ProcessDocumentStorageCleanupHandler(
    IDocumentStorageCleanupRepository cleanupRepository,
    IOptions<DocumentIngestionOptions> ingestionOptions,
    DocumentStorageCleanupRequestProcessor requestProcessor)
    : IRequestHandler<ProcessDocumentStorageCleanupCommand, ProcessDocumentStorageCleanupResponse>
{
    public async Task<ProcessDocumentStorageCleanupResponse> HandleAsync(
        ProcessDocumentStorageCleanupCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);

        var options = ingestionOptions.Value;
        var maxRequests = DocumentStorageCleanupLimits.ResolveMaxRequests(
            request.MaxRequests,
            options.MaxStorageCleanupRequestsPerPoll);
        var cleanupRequests = await cleanupRepository.ClaimBatchAsync(
            request.WorkerId,
            maxRequests,
            TimeSpan.FromSeconds(Math.Max(1, options.ProcessingJobLeaseSeconds)),
            cancellationToken);

        var deleted = 0;
        var deferred = 0;
        var failed = 0;

        foreach (var cleanupRequest in cleanupRequests)
        {
            switch (await requestProcessor.ProcessAsync(cleanupRequest, cancellationToken))
            {
                case DocumentStorageCleanupOutcome.Deleted:
                    deleted++;
                    break;
                case DocumentStorageCleanupOutcome.Deferred:
                    deferred++;
                    break;
                case DocumentStorageCleanupOutcome.Failed:
                    failed++;
                    break;
            }
        }

        return new ProcessDocumentStorageCleanupResponse(
            cleanupRequests.Count,
            deleted,
            deferred,
            failed);
    }
}
