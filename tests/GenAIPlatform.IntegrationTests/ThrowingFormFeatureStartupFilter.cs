using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace GenAIPlatform.IntegrationTests;

internal sealed class ThrowingFormFeatureStartupFilter(Exception exception)
    : IStartupFilter, IFormFeature
{
    public bool HasFormContentType => true;

    public IFormCollection? Form { get; set; }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return application =>
        {
            application.Use((context, nextMiddleware) =>
            {
                if (context.Request.Path == "/api/v1/documents")
                {
                    context.Features.Set<IFormFeature>(this);
                }

                return nextMiddleware(context);
            });
            next(application);
        };
    }

    public IFormCollection ReadForm() => throw exception;

    public Task<IFormCollection> ReadFormAsync(CancellationToken cancellationToken) =>
        Task.FromException<IFormCollection>(exception);
}
