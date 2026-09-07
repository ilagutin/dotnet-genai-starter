using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Security;

namespace GenAIPlatform.Application.Knowledge.Documents;

public sealed class GetDocumentStatusHandler(
    IDocumentIngestionRepository repository,
    IUserContext userContext)
    : IRequestHandler<GetDocumentStatusQuery, DocumentStatusResponse?>
{
    public async Task<DocumentStatusResponse?> HandleAsync(
        GetDocumentStatusQuery request,
        CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated ||
            string.IsNullOrWhiteSpace(userContext.TenantId) ||
            string.IsNullOrWhiteSpace(userContext.UserId))
        {
            return null;
        }

        var snapshot = await repository.GetDocumentStatusAsync(
            request.DocumentId,
            userContext.TenantId,
            userContext.UserId,
            cancellationToken);

        if (snapshot is null)
        {
            return null;
        }

        var document = snapshot.Document;
        var job = snapshot.LatestJob;

        return new DocumentStatusResponse(
            document.Id,
            document.Title,
            document.FileName,
            document.Version,
            document.AccessLevel.ToString(),
            document.IndexingStatus.ToString(),
            job?.Id,
            job?.Status.ToString(),
            job?.Attempts ?? 0,
            snapshot.ChunkCount,
            document.FailureReason ?? job?.FailureReason,
            document.UpdatedAtUtc);
    }
}
