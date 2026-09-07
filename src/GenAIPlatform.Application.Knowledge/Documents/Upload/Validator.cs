using FluentValidation;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents;

internal sealed class UploadDocumentValidator : AbstractValidator<UploadDocumentCommand>
{
    private readonly HashSet<string> allowedExtensions;

    public UploadDocumentValidator(IOptions<DocumentIngestionOptions> options)
    {
        var ingestionOptions = options.Value;
        allowedExtensions = ingestionOptions.AllowedExtensions
            .Select(static value => value.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        RuleFor(request => request.FileName)
            .Custom(ValidateFileName);

        RuleFor(request => request.Length)
            .Custom((length, context) => ValidateLength(
                length,
                ingestionOptions.MaxUploadBytes,
                context));

        RuleFor(request => request.AccessLevel)
            .Must(HasValidAccessLevel)
            .WithMessage("Document access level must be 'Private' or 'TenantPublic'.");
    }

    private void ValidateFileName(
        string? fileName,
        ValidationContext<UploadDocumentCommand> context)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            context.AddFailure("Document file name is required.");
            return;
        }

        var safeFileName = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            context.AddFailure("Document file name is invalid.");
            return;
        }

        var normalizedExtension = Path.GetExtension(safeFileName).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedExtension))
        {
            context.AddFailure("Document file extension is required.");
            return;
        }

        if (!allowedExtensions.Contains(normalizedExtension))
        {
            context.AddFailure($"Document extension '{normalizedExtension}' is not supported.");
        }
    }

    private static void ValidateLength(
        long? length,
        long maxUploadBytes,
        ValidationContext<UploadDocumentCommand> context)
    {
        if (length is null)
        {
            return;
        }

        if (length <= 0)
        {
            context.AddFailure("Document file is empty.");
            return;
        }

        if (maxUploadBytes > 0 && length > maxUploadBytes)
        {
            throw new DocumentTooLargeException(
                $"Document file must be {maxUploadBytes} bytes or fewer.");
        }
    }

    private static bool HasValidAccessLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var normalizedValue = value.Trim();
        return normalizedValue.Equals(nameof(DocumentAccessLevel.Private), StringComparison.OrdinalIgnoreCase) ||
               normalizedValue.Equals(nameof(DocumentAccessLevel.TenantPublic), StringComparison.OrdinalIgnoreCase);
    }
}
