using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Knowledge.Documents;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Api;

internal static class DocumentEndpoints
{
    public static RouteGroupBuilder MapDocumentEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/documents", UploadDocument)
            .WithName("UploadDocument")
            .WithSummary("Upload a document for ingestion (indexed asynchronously by the worker).")
            .Accepts<IFormFile>("multipart/form-data")
            .Produces<UploadDocumentResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge);

        api.MapGet("/documents/{documentId:guid}", GetDocumentStatus)
            .WithName("GetDocumentStatus")
            .WithSummary("Get the indexing status of a previously uploaded document.")
            .Produces<DocumentStatusResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    private static async Task<IResult> UploadDocument(
        HttpRequest httpRequest,
        IApplicationDispatcher dispatcher,
        IOptions<DocumentIngestionOptions> ingestionOptions,
        IOptions<FormOptions> formOptions,
        CancellationToken cancellationToken)
    {
        if (!httpRequest.HasFormContentType)
        {
            return ApiErrorMapping.BadRequest("multipart/form-data is required");
        }

        try
        {
            var form = await httpRequest.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file");
            if (file is null)
            {
                return ApiErrorMapping.BadRequest("file is required");
            }

            await using var content = file.OpenReadStream();
            var result = await dispatcher.DispatchAsync<UploadDocumentCommand, UploadDocumentResponse>(
                new UploadDocumentCommand(
                    file.FileName,
                    file.ContentType,
                    file.Length,
                    form.TryGetValue("title", out var title) ? title.ToString() : null,
                    form.TryGetValue("accessLevel", out var accessLevel) ? accessLevel.ToString() : null,
                    content),
                cancellationToken);

            return Results.Accepted($"/api/v1/documents/{result.DocumentId}", result);
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ApiErrorMapping.PayloadTooLarge(ingestionOptions.Value.MaxUploadBytes);
        }
        catch (InvalidDataException)
            when (httpRequest.ContentLength > formOptions.Value.MultipartBodyLengthLimit)
        {
            return ApiErrorMapping.PayloadTooLarge(ingestionOptions.Value.MaxUploadBytes);
        }
        catch (InvalidDataException)
        {
            return ApiErrorMapping.BadRequest("multipart/form-data is invalid");
        }
    }

    private static async Task<IResult> GetDocumentStatus(
        Guid documentId,
        IApplicationDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var result = await dispatcher.DispatchAsync<GetDocumentStatusQuery, DocumentStatusResponse?>(
            new GetDocumentStatusQuery(documentId),
            cancellationToken);

        return result is null
            ? ApiErrorMapping.NotFound("document was not found")
            : Results.Ok(result);
    }
}
