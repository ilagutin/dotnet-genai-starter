using GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Failure;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;

internal sealed class IndexingJobCounters(int expiredOrExhaustedFailed)
{
    private int claimed;
    private int indexed;
    private int failed;
    private int retried;

    public void MarkClaimed()
    {
        claimed++;
    }

    public void Mark(IndexingJobProcessingResult result)
    {
        if (result == IndexingJobProcessingResult.Indexed)
        {
            indexed++;
        }
        else if (result == IndexingJobProcessingResult.Failed)
        {
            failed++;
        }
    }

    public void Mark(IndexingFailureRecord failureRecord)
    {
        if (failureRecord == IndexingFailureRecord.Retried)
        {
            retried++;
        }
        else if (failureRecord == IndexingFailureRecord.Failed)
        {
            failed++;
        }
    }

    public ProcessIndexingJobsResponse ToResponse()
    {
        return new ProcessIndexingJobsResponse(
            claimed,
            indexed,
            failed,
            retried,
            expiredOrExhaustedFailed);
    }
}
