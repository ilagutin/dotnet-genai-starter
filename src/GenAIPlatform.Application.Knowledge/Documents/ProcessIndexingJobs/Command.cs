using GenAIPlatform.Application.Core.Dispatching;

namespace GenAIPlatform.Application.Knowledge.Documents.ProcessIndexingJobs;

public sealed record ProcessIndexingJobsCommand(
    string WorkerId,
    int? MaxJobs,
    string? CorrelationId = null)
    : IRequest<ProcessIndexingJobsResponse>;
