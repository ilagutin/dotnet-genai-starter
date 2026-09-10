using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GenAIPlatform.IntegrationTests;

internal static class LoopbackTestServer
{
    public static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            // Pin the content root to the test binaries so this fake server is immune to
            // other tests that mutate the process-global Environment.CurrentDirectory
            // (e.g. HostCompositionTests). Without this, parallel runs can fail with
            // "content root does not exist" when that directory is mid-cleanup.
            ContentRootPath = AppContext.BaseDirectory
        });

        // Direct Kestrel binding avoids ASPNETCORE_HTTP_PORTS overrides in SDK/container environments.
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        return builder;
    }

    public static string GetAddress(WebApplication app)
    {
        var addresses = app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()
            ?.Addresses;

        return addresses?.SingleOrDefault(IsLoopbackAddress)
            ?? throw new InvalidOperationException("Test server did not expose a loopback listen address.");
    }

    private static bool IsLoopbackAddress(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
               uri.IsLoopback;
    }
}
