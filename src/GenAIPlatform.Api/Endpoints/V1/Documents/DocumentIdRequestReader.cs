using System.Text.Json;
using GenAIPlatform.Application.Generation.ModelGateway;

namespace GenAIPlatform.Api;

internal static class DocumentIdRequestReader
{
    public static IReadOnlyCollection<Guid>? ReadDocumentIds(JsonElement documentIds)
    {
        if (documentIds.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (documentIds.ValueKind == JsonValueKind.Null)
        {
            throw new ModelRequestValidationException(
                "DocumentIds must be omitted or contain at least one id.");
        }

        if (documentIds.ValueKind != JsonValueKind.Array)
        {
            throw new ModelRequestValidationException(
                "DocumentIds must be an array of GUID values.");
        }

        var parsedDocumentIds = new List<Guid>();
        foreach (var documentId in documentIds.EnumerateArray())
        {
            if (documentId.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(documentId.GetString(), out var parsedDocumentId))
            {
                throw new ModelRequestValidationException(
                    "DocumentIds must contain GUID values.");
            }

            parsedDocumentIds.Add(parsedDocumentId);
        }

        if (parsedDocumentIds.Count == 0)
        {
            throw new ModelRequestValidationException(
                "DocumentIds must be omitted or contain at least one id.");
        }

        return parsedDocumentIds;
    }
}
