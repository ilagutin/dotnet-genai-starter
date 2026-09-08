using GenAIPlatform.Application.Core;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Generation;
using GenAIPlatform.Application.Knowledge;
using GenAIPlatform.Evaluations;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var command = args.FirstOrDefault() ?? "run";
if (!string.Equals(command, "run", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: dotnet run --project src/GenAIPlatform.Evaluations -- run");
    return 2;
}

var builder = EvaluationCliHost.CreateBuilder(args);
builder.Services.AddApplicationCore(builder.Configuration);
builder.Services.AddKnowledgeApplication(builder.Configuration);
builder.Services.AddGenerationApplication(builder.Configuration);
builder.Services.AddEvaluationsApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEvaluations();

try
{
    using var host = builder.Build();
    using var scope = host.Services.CreateScope();
    var dispatcher = scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>();

    return await EvaluationCliRunner.RunAsync(dispatcher, Console.Out, CancellationToken.None);
}
catch (InvalidOperationException exception)
    when (exception.Message.StartsWith("PostgreSQL connection string ", StringComparison.Ordinal))
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

public static class EvaluationCliHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args, string? contentRootPath = null)
    {
        var configurationArgs = GetConfigurationArgs(args);

        return Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = configurationArgs,
            ContentRootPath = contentRootPath ?? AppContext.BaseDirectory
        });
    }

    private static string[] GetConfigurationArgs(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            return args[1..];
        }

        return args;
    }
}
