namespace GenAIPlatform.Application.Core.Configuration;

public sealed class ApplicationOptions
{
    public const string SectionName = "GenAIPlatform:Application";

    [RequiredNonBlank]
    public string ApiVersion { get; init; } = "v1";

    [RequiredNonBlank]
    public string RunnerVersion { get; init; } = "0.3.0-dev";
}
