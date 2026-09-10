using GenAIPlatform.Infrastructure;
using GenAIPlatform.Infrastructure.Configuration;
using GenAIPlatform.Infrastructure.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Migrations;

/// <summary>
/// Composes the one-shot migration host and runs a single verb. The host is deliberately never
/// started, so nothing here can rely on startup validation: migration configuration is resolved
/// eagerly and a configuration failure becomes an exit code and one sanitized line, not an
/// unhandled exception with a stack trace.
/// </summary>
public static class MigrationCliApplication
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var parsed = MigrationCliArgumentParser.Parse(args);
        if (parsed.Arguments is null)
        {
            await error.WriteLineAsync(parsed.Error);
            await error.WriteLineAsync(MigrationCliVerbs.UsageText);
            return MigrationCliExitCodes.UsageOrConfiguration;
        }

        var builder = MigrationCliHost.CreateBuilder(parsed.Arguments.ConfigurationArgs);
        builder.Services.AddInfrastructure(builder.Configuration);
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();

        try
        {
            // ValidateOnStart never runs for an unstarted host, so force the same validation here.
            _ = scope.ServiceProvider.GetRequiredService<IOptions<MigrationOptions>>().Value;
        }
        catch (OptionsValidationException exception)
        {
            await error.WriteLineAsync(SingleLine(string.Join(" ", exception.Failures)));
            return MigrationCliExitCodes.UsageOrConfiguration;
        }
        catch (InvalidOperationException)
        {
            // A value under the section could not be bound at all. The value itself is never
            // echoed back, because configuration may carry credentials.
            await error.WriteLineAsync(
                $"Schema migration configuration is invalid. Check the {MigrationOptions.SectionName} " +
                "section: LockTimeout must be a positive duration such as 00:00:30 and " +
                "CommandTimeoutSeconds a positive whole number of seconds.");
            return MigrationCliExitCodes.UsageOrConfiguration;
        }

        return await MigrationCliRunner.RunAsync(
            scope.ServiceProvider.GetRequiredService<ISchemaMigrator>(),
            parsed.Arguments.Verb,
            output,
            error,
            cancellationToken);
    }

    private static string SingleLine(string message)
    {
        return string.Join(' ', message.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
