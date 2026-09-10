using Microsoft.Extensions.Hosting;

namespace GenAIPlatform.Migrations;

/// <summary>
/// Builds the one-shot migration host. It composes Infrastructure only: no Application module is
/// registered, and the host is never started, so nothing migrates as a startup side effect.
/// </summary>
public static class MigrationCliHost
{
    public static HostApplicationBuilder CreateBuilder(
        string[] configurationArgs,
        string? contentRootPath = null)
    {
        return Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = configurationArgs,
            ContentRootPath = contentRootPath ?? AppContext.BaseDirectory
        });
    }
}
