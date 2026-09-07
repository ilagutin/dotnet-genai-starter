using GenAIPlatform.Api.Security;
using GenAIPlatform.Application.Knowledge.Documents;
using Microsoft.AspNetCore.Http.Features;

namespace GenAIPlatform.Api;

public static class Setup
{
    private const long MultipartFormOverheadBytes = 128 * 1024;

    public static IServiceCollection AddApi(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        services.Configure<FormOptions>(options =>
        {
            var ingestionOptions = configuration
                .GetSection(DocumentIngestionOptions.SectionName)
                .Get<DocumentIngestionOptions>() ?? new DocumentIngestionOptions();

            if (ingestionOptions.MaxUploadBytes > 0)
            {
                options.MultipartBodyLengthLimit =
                    ingestionOptions.MaxUploadBytes > long.MaxValue - MultipartFormOverheadBytes
                        ? long.MaxValue
                        : ingestionOptions.MaxUploadBytes + MultipartFormOverheadBytes;
            }
        });
        services.AddHttpContextAccessor();
        services.AddApiUserContext(configuration, environment);
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddProblemDetails();
        services.AddHealthChecks();
        services.AddOpenApi();

        return services;
    }
}
