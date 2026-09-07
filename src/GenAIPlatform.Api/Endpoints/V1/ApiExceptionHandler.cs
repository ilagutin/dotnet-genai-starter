using GenAIPlatform.Application.Core.Errors;
using GenAIPlatform.Application.Core.Exceptions;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Api;

internal sealed class ApiExceptionHandler(
    IOptions<DocumentIngestionOptions> ingestionOptions,
    ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var result = MapException(exception);
        if (result is null)
        {
            return false;
        }

        await result.ExecuteAsync(httpContext);
        return true;
    }

    private IResult? MapException(Exception exception)
    {
        if (exception is DomainException and not EvaluationValidationException)
        {
            logger.LogError(exception, "A domain exception reached the API error boundary.");
        }

        return exception switch
        {
            UnauthorizedRequestException current => ApiErrorMapping.Unauthorized(current),
            ForbiddenRequestException current => ApiErrorMapping.Forbidden(current),
            NotFoundException current => ApiErrorMapping.NotFound(current),
            ConflictException current => ApiErrorMapping.Conflict(current),
            DocumentTooLargeException current => ApiErrorMapping.PayloadTooLarge(
                current,
                ingestionOptions.Value.MaxUploadBytes),
            ProviderException current => ApiErrorMapping.ProviderProblem(current),
            ValidationException current => ApiErrorMapping.BadRequest(current.Message),
            EvaluationValidationException current => ApiErrorMapping.BadRequest(current.Message),
            DomainException current => ApiErrorMapping.InternalDomainViolation(current),
            _ => null
        };
    }
}
