using GenAIPlatform.Application.Core.Exceptions;
using GenAIPlatform.Application.Core.Security;

namespace GenAIPlatform.Application.Usage.GetUsage;

public sealed class UsageQueryScopeResolver(IUserContext userContext)
{
    public UsageQuery Resolve(UsageQuery request)
    {
        if (!userContext.IsAuthenticated)
        {
            throw new UnauthorizedRequestException("An authenticated user is required.");
        }

        if (request.FromUtc is not null &&
            request.ToUtc is not null &&
            request.FromUtc > request.ToUtc)
        {
            throw new UsageQueryValidationException("from must be before or equal to to.");
        }

        return IsAdmin()
            ? request
            : ScopeToCurrentUser(request);
    }

    private UsageQuery ScopeToCurrentUser(UsageQuery request)
    {
        if (string.IsNullOrWhiteSpace(userContext.TenantId) ||
            string.IsNullOrWhiteSpace(userContext.UserId))
        {
            throw new UnauthorizedRequestException("An authenticated user and tenant are required.");
        }

        if (!string.IsNullOrWhiteSpace(request.TenantId) &&
            !string.Equals(request.TenantId, userContext.TenantId, StringComparison.Ordinal))
        {
            throw new ForbiddenRequestException("Usage tenant filter must match the authenticated tenant.");
        }

        if (!string.IsNullOrWhiteSpace(request.UserId) &&
            !string.Equals(request.UserId, userContext.UserId, StringComparison.Ordinal))
        {
            throw new ForbiddenRequestException("Usage user filter must match the authenticated user.");
        }

        return request with
        {
            UserId = userContext.UserId,
            TenantId = userContext.TenantId
        };
    }

    private bool IsAdmin()
    {
        return userContext.Roles.Any(static role =>
            string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase));
    }
}
