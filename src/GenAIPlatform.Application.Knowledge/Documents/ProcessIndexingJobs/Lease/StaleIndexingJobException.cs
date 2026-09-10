namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs.Lease;

internal sealed class StaleIndexingJobException : Exception
{
    public StaleIndexingJobException()
        : base("Indexing job is no longer owned by this worker.")
    {
    }
}
