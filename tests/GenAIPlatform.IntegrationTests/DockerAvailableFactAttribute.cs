using System.Runtime.CompilerServices;

namespace GenAIPlatform.IntegrationTests;

public sealed class DockerAvailableFactAttribute : FactAttribute
{
    public DockerAvailableFactAttribute(
        [CallerFilePath] string sourceFilePath = "",
        [CallerLineNumber] int sourceLineNumber = 0)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!IsDockerEndpointLikelyAvailable() && !IsDockerRequiredEnvironment())
        {
            Skip = "Docker is not available for PostgreSQL integration tests.";
        }
    }

    private static bool IsDockerEndpointLikelyAvailable()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOCKER_HOST")))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return CanConnectToNamedPipe("docker_engine") ||
                   CanConnectToNamedPipe("dockerDesktopLinuxEngine");
        }

        return File.Exists("/var/run/docker.sock") ||
               File.Exists(Path.Combine(
                   Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                   ".docker/run/docker.sock"));
    }

    private static bool CanConnectToNamedPipe(string pipeName)
    {
        try
        {
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                ".",
                pipeName,
                System.IO.Pipes.PipeDirection.InOut);
            pipe.Connect(100);
            return pipe.IsConnected;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDockerRequiredEnvironment()
    {
        return IsTruthy(Environment.GetEnvironmentVariable("CI")) ||
               IsTruthy(Environment.GetEnvironmentVariable("GENAI_REQUIRE_DOCKER_TESTS"));
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null &&
               (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }
}
