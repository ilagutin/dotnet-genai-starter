namespace GenAIPlatform.Migrations;

/// <summary>
/// Parses the migration CLI command line. The verb is optional and defaults to
/// <see cref="MigrationCliVerbs.Migrate"/>; anything else that is not a known verb is a usage
/// error rather than an unknown configuration key.
/// </summary>
public static class MigrationCliArgumentParser
{
    public static MigrationCliParseResult Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new MigrationCliParseResult(
                new MigrationCliArguments(MigrationCliVerbs.Migrate, []),
                Error: null);
        }

        var first = args[0];
        if (MigrationCliVerbs.IsKnown(first))
        {
            return new MigrationCliParseResult(
                new MigrationCliArguments(first.ToLowerInvariant(), args[1..]),
                Error: null);
        }

        if (IsConfigurationArgument(first))
        {
            return new MigrationCliParseResult(
                new MigrationCliArguments(MigrationCliVerbs.Migrate, args),
                Error: null);
        }

        return new MigrationCliParseResult(
            Arguments: null,
            $"Unknown verb '{first}'.");
    }

    private static bool IsConfigurationArgument(string argument)
    {
        return argument.StartsWith('-') || argument.Contains('=', StringComparison.Ordinal);
    }
}
