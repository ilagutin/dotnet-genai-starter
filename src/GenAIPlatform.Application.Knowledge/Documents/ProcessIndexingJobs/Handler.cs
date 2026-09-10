using GenAIPlatform.Application.Core.Dispatching;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;

internal sealed class ProcessIndexingJobsHandler(IndexingJobBatchProcessor processor)
    : IRequestHandler<ProcessIndexingJobsCommand, ProcessIndexingJobsResponse>
{
    public async Task<ProcessIndexingJobsResponse> HandleAsync(
        ProcessIndexingJobsCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkerId);

        return await processor.ProcessAsync(
            request,
            cancellationToken);
    }
}
