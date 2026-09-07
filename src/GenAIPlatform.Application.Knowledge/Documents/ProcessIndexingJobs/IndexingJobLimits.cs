namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;

internal static class IndexingJobLimits
{
    public static int ResolveMaxJobs(int? requestedMaxJobs, int configuredMaxJobs)
    {
        var maxJobs = requestedMaxJobs ?? configuredMaxJobs;
        return Math.Clamp(maxJobs, 1, 50);
    }
}
