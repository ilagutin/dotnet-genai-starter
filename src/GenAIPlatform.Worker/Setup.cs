using GenAIPlatform.Application.Core.Security;

namespace GenAIPlatform.Worker;

public static class Setup
{
    public static IServiceCollection AddWorker(this IServiceCollection services)
    {
        services.AddScoped<IUserContext>(
            serviceProvider => serviceProvider.GetRequiredService<IBackgroundUserContext>());
        services.AddHostedService<Worker>();

        return services;
    }
}
