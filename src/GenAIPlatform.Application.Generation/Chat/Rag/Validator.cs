using FluentValidation;
using GenAIPlatform.Application.Generation.ModelGateway;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Application.Knowledge.Retrieval;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Generation.Chat;

internal sealed class RagChatValidator : AbstractValidator<RagChatCommand>
{
    public RagChatValidator(
        ModelGatewayRequestPolicy modelGatewayRequestPolicy,
        IOptions<EmbeddingOptions> embeddingOptions,
        IOptions<RagOptions> ragOptions)
    {
        var maxInputMessageCharacters = modelGatewayRequestPolicy.GetMaxInputMessageCharacters();
        var effectiveMaxCharacters = ResolveEffectiveMaxCharacters(
            maxInputMessageCharacters,
            embeddingOptions.Value.MaxInputCharacters);
        var maxTopK = Math.Max(1, ragOptions.Value.MaxTopK);
        var maxDocumentFilters = Math.Clamp(
            ragOptions.Value.MaxDocumentFilters,
            1,
            RagVectorSearchQuery.MaxDocumentFilters);

        RuleFor(request => request.Message)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Message must not be empty.")
            .Must(static message => !string.IsNullOrWhiteSpace(message))
                .WithMessage("Message must not be empty.")
            .Must(message => message is null || message.Trim().Length <= maxInputMessageCharacters)
                .WithMessage($"Message must be {maxInputMessageCharacters} characters or fewer.")
            .Must(message => message is null || message.Trim().Length <= effectiveMaxCharacters)
                .WithMessage($"RAG message must be {effectiveMaxCharacters} characters or fewer.");

        RuleFor(request => request.TopK)
            .Must(topK =>
            {
                var effectiveTopK = topK ?? ragOptions.Value.DefaultTopK;
                return effectiveTopK >= 1 && effectiveTopK <= maxTopK;
            })
            .WithMessage($"TopK must be between 1 and {maxTopK}.");

        RuleFor(request => request.MinSimilarityScore)
            .Must(score => IsValidSimilarityScore(score ?? ragOptions.Value.DefaultMinSimilarityScore))
            .WithMessage("Minimum similarity score must be between -1 and 1.");

        RuleFor(request => request.DocumentIds)
            .Must(documentIds => documentIds is null || documentIds.Count > 0)
            .WithMessage("DocumentIds must be omitted or contain at least one id.")
            .Must(documentIds => documentIds is null || documentIds.Count <= maxDocumentFilters)
            .WithMessage($"DocumentIds must contain {maxDocumentFilters} ids or fewer.")
            .Must(documentIds => documentIds is null || documentIds.All(static documentId => documentId != Guid.Empty))
            .WithMessage("DocumentIds must not contain empty GUID values.");
    }

    private static int ResolveEffectiveMaxCharacters(
        int modelMaxCharacters,
        int embeddingMaxCharacters)
    {
        return embeddingMaxCharacters <= 0
            ? modelMaxCharacters
            : Math.Min(modelMaxCharacters, embeddingMaxCharacters);
    }

    private static bool IsValidSimilarityScore(double score)
    {
        return !double.IsNaN(score) &&
               !double.IsInfinity(score) &&
               score is >= -1 and <= 1;
    }
}
