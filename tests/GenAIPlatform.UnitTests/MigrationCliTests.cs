using GenAIPlatform.Infrastructure.Migrations;
using GenAIPlatform.Migrations;

namespace GenAIPlatform.UnitTests;

public sealed class MigrationCliTests
{
    [Fact]
    public void Parse_DefaultsToMigrateWhenNoVerbIsGiven()
    {
        var parsed = MigrationCliArgumentParser.Parse([]);

        Assert.Null(parsed.Error);
        Assert.Equal(MigrationCliVerbs.Migrate, parsed.Arguments!.Verb);
        Assert.Empty(parsed.Arguments.ConfigurationArgs);
    }

    [Theory]
    [InlineData("migrate")]
    [InlineData("MIGRATE")]
    [InlineData("status")]
    public void Parse_AcceptsKnownVerbsCaseInsensitively(string verb)
    {
        var parsed = MigrationCliArgumentParser.Parse([verb]);

        Assert.Null(parsed.Error);
        Assert.Equal(verb.ToLowerInvariant(), parsed.Arguments!.Verb);
    }

    [Fact]
    public void Parse_PassesConfigurationArgumentsThroughWithoutAVerb()
    {
        var parsed = MigrationCliArgumentParser.Parse(
            ["--GenAIPlatform:Migrations:LockTimeout=00:00:05"]);

        Assert.Null(parsed.Error);
        Assert.Equal(MigrationCliVerbs.Migrate, parsed.Arguments!.Verb);
        Assert.Single(parsed.Arguments.ConfigurationArgs);
    }

    [Fact]
    public void Parse_RejectsAnUnknownVerb()
    {
        var parsed = MigrationCliArgumentParser.Parse(["rollback"]);

        Assert.Null(parsed.Arguments);
        Assert.Contains("rollback", parsed.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsUpToDateWhenMigrationsAreApplied()
    {
        var migrator = new FakeSchemaMigrator
        {
            Result = new SchemaMigrationResult(["0005", "0006"], false, "0006")
        };
        var output = new StringWriter();

        var exitCode = await MigrationCliRunner.RunAsync(
            migrator,
            MigrationCliVerbs.Migrate,
            output,
            TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.Equal(MigrationCliExitCodes.UpToDate, exitCode);
        Assert.Contains("0005, 0006", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReportsAdoptionOfTheFrozenLegacySchema()
    {
        var migrator = new FakeSchemaMigrator
        {
            Result = new SchemaMigrationResult([], AdoptedLegacySchema: true, "0006")
        };
        var output = new StringWriter();

        var exitCode = await MigrationCliRunner.RunAsync(
            migrator,
            MigrationCliVerbs.Migrate,
            output,
            TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.Equal(MigrationCliExitCodes.UpToDate, exitCode);
        Assert.Contains("Adopted", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("up to date", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureAndPrintsTheSanitizedErrorCode()
    {
        var migrator = new FakeSchemaMigrator
        {
            Failure = new SchemaMigrationException(
                "Another schema migration run holds the lock.",
                SchemaMigrationErrorCodes.LockTimeout)
        };
        var error = new StringWriter();

        var exitCode = await MigrationCliRunner.RunAsync(
            migrator,
            MigrationCliVerbs.Migrate,
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(MigrationCliExitCodes.BehindOrFailed, exitCode);
        Assert.Contains(
            SchemaMigrationErrorCodes.LockTimeout,
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsBehindWhenStatusHasPendingVersions()
    {
        var migrator = new FakeSchemaMigrator
        {
            Status = new SchemaMigrationStatus(
                JournalPresent: true,
                JournalHead: "0004",
                PackagedHead: "0006",
                ["0005", "0006"],
                [])
        };
        var output = new StringWriter();

        var exitCode = await MigrationCliRunner.RunAsync(
            migrator,
            MigrationCliVerbs.Status,
            output,
            TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.Equal(MigrationCliExitCodes.BehindOrFailed, exitCode);
        Assert.Contains("Pending: 0005, 0006", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsUpToDateForACurrentStatus()
    {
        var output = new StringWriter();

        var exitCode = await MigrationCliRunner.RunAsync(
            new FakeSchemaMigrator(),
            MigrationCliVerbs.Status,
            output,
            TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.Equal(MigrationCliExitCodes.UpToDate, exitCode);
        Assert.Contains("Schema is up to date.", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MigrationCliVerbs.Migrate)]
    [InlineData(MigrationCliVerbs.Status)]
    public async Task RunAsync_ReturnsAConfigurationExitCodeWhenTheDatabaseIsNotConfigured(
        string verb)
    {
        var migrator = new FakeSchemaMigrator
        {
            Failure = new SchemaMigrationException(
                "PostgreSQL connection string 'GenAIPlatform' is not configured.",
                SchemaMigrationErrorCodes.NotConfigured)
        };
        var error = new StringWriter();

        var exitCode = await MigrationCliRunner.RunAsync(
            migrator,
            verb,
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(MigrationCliExitCodes.UsageOrConfiguration, exitCode);
        Assert.Contains(
            SchemaMigrationErrorCodes.NotConfigured,
            error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReturnsFailureAndOneLineWhenTheRunIsCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var error = new StringWriter();

        var exitCode = await MigrationCliRunner.RunAsync(
            new FakeSchemaMigrator(),
            MigrationCliVerbs.Migrate,
            TextWriter.Null,
            error,
            cancellation.Token);

        Assert.Equal(MigrationCliExitCodes.BehindOrFailed, exitCode);
        var line = Assert.Single(SplitLines(error));
        Assert.Contains("canceled", line, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Application_RejectsAnInvalidLockTimeoutWithAConfigurationExitCode()
    {
        var error = new StringWriter();

        var exitCode = await MigrationCliApplication.RunAsync(
            ["status", "--GenAIPlatform:Migrations:LockTimeout=00:00:00"],
            TextWriter.Null,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(MigrationCliExitCodes.UsageOrConfiguration, exitCode);
        var line = Assert.Single(SplitLines(error));
        Assert.Contains("GenAIPlatform:Migrations:LockTimeout", line, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ReportsJournalMismatchesInStatus()
    {
        var migrator = new FakeSchemaMigrator
        {
            Status = new SchemaMigrationStatus(
                JournalPresent: true,
                JournalHead: "0006",
                PackagedHead: "0006",
                [],
                ["Migration '0006' was applied from a different script than the packaged one."])
        };
        var output = new StringWriter();

        var exitCode = await MigrationCliRunner.RunAsync(
            migrator,
            MigrationCliVerbs.Status,
            output,
            TextWriter.Null,
            TestContext.Current.CancellationToken);

        Assert.Equal(MigrationCliExitCodes.BehindOrFailed, exitCode);
        Assert.Contains("Mismatch:", output.ToString(), StringComparison.Ordinal);
    }

    private static string[] SplitLines(StringWriter writer)
    {
        return writer.ToString().Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
