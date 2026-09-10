using Microsoft.Extensions.DependencyInjection;

namespace GenAIPlatform.Application.Core.Dispatching;

public sealed class ApplicationDispatcher(IServiceProvider serviceProvider) : IApplicationDispatcher
{
    public Task<TResponse> DispatchAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : IRequest<TResponse>
    {
        ArgumentNullException.ThrowIfNull(request);

        var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
        RequestHandlerDelegate<TResponse> next = () => handler.HandleAsync(request, cancellationToken);

        var behaviors = serviceProvider
            .GetServices<IPipelineBehavior<TRequest, TResponse>>()
            .ToArray();

        if (behaviors.Length == 0)
        {
            return next();
        }

        for (var index = behaviors.Length - 1; index >= 0; index--)
        {
            var behavior = behaviors[index];
            var inner = next;
            next = () => behavior.HandleAsync(request, inner, cancellationToken);
        }

        return next();
    }
}
