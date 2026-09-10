using FluentValidation;
using FluentValidation.Results;

namespace GenAIPlatform.Application.Core.Dispatching;

internal sealed class RequestValidationBehavior<TRequest, TResponse>(
    IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var validatorList = validators as IReadOnlyCollection<IValidator<TRequest>> ?? validators.ToArray();
        if (validatorList.Count == 0)
        {
            return await next();
        }

        // Validators run sequentially rather than via Task.WhenAll on purpose:
        // FluentValidation rules here are synchronous, so there is no parallelism to gain,
        // and awaiting Task.WhenAll would rethrow only the first exception while discarding
        // the rest. Sequential awaiting lets a validator that throws a typed exception
        // (for example UploadDocumentValidator throwing DocumentTooLargeException for a 413)
        // propagate immediately and unambiguously, and accumulates AddFailure results in order.
        var context = new ValidationContext<TRequest>(request);
        var failures = new List<ValidationFailure>();
        foreach (var validator in validatorList)
        {
            var result = await validator.ValidateAsync(context, cancellationToken);
            if (result.Errors.Count > 0)
            {
                failures.AddRange(result.Errors);
            }
        }

        if (failures.Count > 0)
        {
            throw new RequestValidationException(failures);
        }

        return await next();
    }
}
