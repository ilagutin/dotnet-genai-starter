using FluentValidation;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

internal sealed class RunRetrievalBaselineValidator : AbstractValidator<RunRetrievalBaselineCommand>
{
    public const int MaxIdentifierLength = 128;

    public RunRetrievalBaselineValidator()
    {
        RuleFor(request => request.DatasetVersion)
            .Must(IsUsableIdentifier)
            .WithMessage($"Dataset version must be non-blank and at most {MaxIdentifierLength} characters.");

        RuleFor(request => request.CodeRevision)
            .Must(IsUsableIdentifier)
            .WithMessage($"Code revision must be non-blank and at most {MaxIdentifierLength} characters.");
    }

    private static bool IsUsableIdentifier(string? value)
    {
        return value is null ||
               (!string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxIdentifierLength);
    }
}
