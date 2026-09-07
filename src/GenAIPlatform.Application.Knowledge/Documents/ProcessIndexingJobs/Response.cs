namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;

public sealed record ProcessIndexingJobsResponse(
    int Claimed,
    int Indexed,
    int Failed,
    int Retried,
    int ExpiredOrExhaustedFailed);
