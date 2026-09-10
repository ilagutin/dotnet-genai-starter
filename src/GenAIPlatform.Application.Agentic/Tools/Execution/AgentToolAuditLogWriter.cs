namespace GenAIPlatform.Application.Agentic.Tools.Execution;

internal sealed class AgentToolAuditLogWriter(
    IToolAuditLogRepository auditLogRepository,
    TimeProvider timeProvider)
{
    public Task WriteAsync(
        AgentToolExecutionResult result,
        AgentToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        return auditLogRepository.AddAsync(
            AgentToolAuditProjection.Create(result, context, timeProvider.GetUtcNow()),
            cancellationToken);
    }
}
