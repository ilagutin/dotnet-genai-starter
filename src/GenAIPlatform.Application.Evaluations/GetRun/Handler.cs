using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Evaluations.StartRun;

namespace GenAIPlatform.Application.Evaluations;

public sealed class GetEvaluationRunHandler(
    IEvaluationRunRepository repository,
    IUserContext userContext)
    : IRequestHandler<GetEvaluationRunQuery, EvaluationRunResult?>
{
    public Task<EvaluationRunResult?> HandleAsync(
        GetEvaluationRunQuery request,
        CancellationToken cancellationToken)
    {
        var tenantId = userContext.TenantId;
        var userId = userContext.UserId;
        if (!userContext.IsAuthenticated ||
            string.IsNullOrWhiteSpace(tenantId) ||
            string.IsNullOrWhiteSpace(userId))
        {
            return Task.FromResult<EvaluationRunResult?>(null);
        }

        return repository.GetRunAsync(request.RunId, tenantId, userId, cancellationToken);
    }
}
