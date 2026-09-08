using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace GenAIPlatform.IntegrationTests;

public sealed class ConfigurationSecretTests
{
    [Theory]
    [InlineData("src/GenAIPlatform.Api/appsettings.json")]
    [InlineData("src/GenAIPlatform.Mcp/appsettings.json")]
    public async Task RunnerVersion_MatchesCurrentDevelopmentVersion(string relativePath)
    {
        var configuration = await ReadAppSettingsAsync(relativePath);
        var version = (await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "VERSION"))).Trim() + "-dev";
        Assert.Equal("0.3.0-dev", version);
        Assert.Equal(version, configuration["GenAIPlatform"]!["Application"]!["RunnerVersion"]!.GetValue<string>());
        Assert.Equal(version, new GenAIPlatform.Application.Core.Configuration.ApplicationOptions().RunnerVersion);
    }

    [Fact]
    public async Task WorkerAppSettings_ContainsOnlyConsumedConfigurationMatchingApiValues()
    {
        var apiConfiguration = await ReadAppSettingsAsync("src/GenAIPlatform.Api/appsettings.json");
        var workerConfiguration = await ReadAppSettingsAsync("src/GenAIPlatform.Worker/appsettings.json");
        var apiPlatform = apiConfiguration["GenAIPlatform"]!.AsObject();
        var workerPlatform = workerConfiguration["GenAIPlatform"]!.AsObject();
        var apiIngestion = apiPlatform["DocumentIngestion"]!.AsObject();
        var workerIngestion = workerPlatform["DocumentIngestion"]!.AsObject();

        Assert.Equal(
            ["DocumentIngestion", "DocumentStorage", "Postgres"],
            workerPlatform.Select(static property => property.Key).Order(StringComparer.Ordinal));
        Assert.Equal(
            apiIngestion["ChunkMaxCharacters"]!.GetValue<int>(),
            workerIngestion["ChunkMaxCharacters"]!.GetValue<int>());
        Assert.Equal(
            apiIngestion["AllowedExtensions"]!.AsArray().Select(static value => value!.GetValue<string>()),
            workerIngestion["AllowedExtensions"]!.AsArray().Select(static value => value!.GetValue<string>()));
        Assert.Equal(
            apiPlatform["DocumentStorage"]!["RootPath"]!.GetValue<string>(),
            workerPlatform["DocumentStorage"]!["RootPath"]!.GetValue<string>());
        Assert.Equal(
            apiPlatform["Postgres"]!["ConnectionStringName"]!.GetValue<string>(),
            workerPlatform["Postgres"]!["ConnectionStringName"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("src/GenAIPlatform.Api/appsettings.json")]
    [InlineData("src/GenAIPlatform.Worker/appsettings.json")]
    [InlineData("src/GenAIPlatform.Evaluations/appsettings.json")]
    public async Task RuntimeAppSettings_DoNotContainPostgresPasswords(string relativePath)
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(FindRepositoryRoot(), relativePath));

        Assert.DoesNotContain("genai_dev_password", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", content, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        foreach (var startPath in new[] { sourceFilePath, AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = File.Exists(startPath)
                ? new FileInfo(startPath).Directory
                : new DirectoryInfo(startPath);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "GenAIPlatform.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new InvalidOperationException("Could not find repository root.");
    }

    private static async Task<JsonObject> ReadAppSettingsAsync(string relativePath)
    {
        var content = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), relativePath));
        return JsonNode.Parse(content)?.AsObject()
               ?? throw new InvalidOperationException($"Could not parse {relativePath}.");
    }
}
