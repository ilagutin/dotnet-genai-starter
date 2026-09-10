using GenAIPlatform.Application.Core;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Evaluations;
using GenAIPlatform.Application.Generation;
using GenAIPlatform.Application.Knowledge;
using GenAIPlatform.Evaluations;
using GenAIPlatform.Evaluations.RetrievalBaseline;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

const int UsageExitCode = 2;

var verb = args.FirstOrDefault() ?? EvaluationCliVerbs.Run;
if (!EvaluationCliVerbs.IsKnown(verb))
{
    Console.Error.WriteLine(EvaluationCliVerbs.UsageText);
    return UsageExitCode;
}

var isRetrievalBaseline = string.Equals(
    verb,
    EvaluationCliVerbs.RetrievalBaseline,
    StringComparison.OrdinalIgnoreCase);
RetrievalBaselineCliOptions? baselineOptions = null;
if (isRetrievalBaseline)
{
    var parsed = RetrievalBaselineCliOptionsParser.Parse(
        args[1..],
        Environment.GetEnvironmentVariable(RetrievalBaselineCliOptionsParser.RevisionEnvironmentVariable));
    if (parsed.Options is null)
    {
        Console.Error.WriteLine(parsed.Error);
        Console.Error.WriteLine(EvaluationCliVerbs.UsageText);
        return UsageExitCode;
    }

    baselineOptions = parsed.Options;
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

    return baselineOptions is null
        ? await EvaluationCliRunner.RunAsync(dispatcher, Console.Out, CancellationToken.None)
        : await RetrievalBaselineCliRunner.RunAsync(
            dispatcher,
            baselineOptions,
            Console.Out,
            Console.Error,
            CancellationToken.None);
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
        var remaining = args.Length > 0 && EvaluationCliVerbs.IsKnown(args[0])
            ? args[1..]
            : args;
        var configurationArgs = new List<string>(remaining.Length);

        for (var index = 0; index < remaining.Length; index++)
        {
            // Verb-owned options are consumed by the CLI itself and must not reach the
            // configuration binder as unknown keys.
            if (RetrievalBaselineCliOptionsParser.IsOwnOption(remaining[index]))
            {
                index++;
                continue;
            }

            configurationArgs.Add(remaining[index]);
        }

        return [.. configurationArgs];
    }
}
