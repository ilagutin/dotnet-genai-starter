using GenAIPlatform.Application.Core.Errors;
using GenAIPlatform.Application.Core.Exceptions;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Knowledge.Retrieval;

namespace GenAIPlatform.Api;

internal static class ApiErrorMapping
{
    public static IResult BadRequest(string error) =>
        Problem("Request validation failed", error, StatusCodes.Status400BadRequest);

    public static IResult Unauthorized(UnauthorizedRequestException exception)
    {
        return Problem(
            "Unauthorized",
            exception.Message,
            StatusCodes.Status401Unauthorized);
    }

    public static IResult Forbidden(ForbiddenRequestException exception)
    {
        return Problem(
            "Forbidden",
            exception.Message,
            StatusCodes.Status403Forbidden);
    }

    public static IResult NotFound(NotFoundException exception) => NotFound(exception.Message);

    public static IResult NotFound(string error)
    {
        return Problem(
            "Not found",
            error,
            StatusCodes.Status404NotFound);
    }

    public static IResult Conflict(ConflictException exception) =>
        Problem("Conflict", exception.Message, StatusCodes.Status409Conflict);

    public static IResult PayloadTooLarge(long maxUploadBytes)
    {
        return Problem(
            "Document payload too large",
            $"Document file must be {maxUploadBytes} bytes or fewer.",
            StatusCodes.Status413PayloadTooLarge);
    }

    public static IResult ProviderProblem(ProviderException exception)
    {
        return Problem(
            ToProviderTitle(exception),
            ToProviderDetail(exception),
            ToProviderStatusCode(exception),
            new Dictionary<string, object?>
            {
                ["provider"] = exception.Provider,
                ["errorCode"] = ToPublicProviderErrorCode(exception),
                ["providerStatusCode"] = exception.StatusCode is null
                    ? null
                    : (int)exception.StatusCode
            });
    }

    public static IResult InternalDomainViolation()
    {
        return Problem(
            "Domain invariant violation",
            "The request could not be completed.",
            StatusCodes.Status500InternalServerError);
    }

    private static IResult Problem(
        string title,
        string detail,
        int statusCode,
        IDictionary<string, object?>? extensions = null)
    {
        return Results.Problem(
            title: title,
            detail: detail,
            statusCode: statusCode,
            extensions: extensions);
    }

    private static string ToProviderTitle(ProviderException exception)
    {
        return exception switch
        {
            AiModelException => "Model provider request failed",
            RagVectorSearchException => "RAG retrieval failed",
            _ => "Embedding provider request failed"
        };
    }

    private static string ToProviderDetail(ProviderException exception)
    {
        return exception switch
        {
            AiModelException => "The upstream model provider request failed.",
            RagVectorSearchException => "The retrieval store could not complete the request.",
            _ => "The upstream embedding provider request failed."
        };
    }

    private static int ToProviderStatusCode(ProviderException exception)
    {
        return exception is RagVectorSearchException
            ? ToRetrievalStatusCode(exception.ErrorCode)
            : StatusCodes.Status502BadGateway;
    }

    private static string ToPublicProviderErrorCode(ProviderException exception)
    {
        return exception switch
        {
            AiModelException => ToPublicGenerationErrorCode(exception.ErrorCode, isModel: true),
            RagVectorSearchException => ToPublicRetrievalErrorCode(exception.ErrorCode),
            _ => ToPublicGenerationErrorCode(exception.ErrorCode, isModel: false)
        };
    }

    private static string ToPublicGenerationErrorCode(string? errorCode, bool isModel)
    {
        return (errorCode, isModel) switch
        {
            ("authentication_error" or
             "configuration_error" or
             "invalid_json" or
             "invalid_request" or
             "provider_timeout" or
             "provider_unavailable" or
             "rate_limited" or
             "timeout" or
             "transport_error", _) => errorCode,
            ("empty_response", true) => errorCode,
            ("empty_embedding" or "invalid_embedding", false) => errorCode,
            _ => "provider_error"
        };
    }

    private static string ToPublicRetrievalErrorCode(string? errorCode)
    {
        return errorCode switch
        {
            "retrieval_unavailable" or
            "retrieval_schema_error" or
            "retrieval_query_failed" => errorCode,
            _ => "retrieval_error"
        };
    }

    private static int ToRetrievalStatusCode(string? errorCode)
    {
        return errorCode == "retrieval_unavailable"
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;
    }
}
