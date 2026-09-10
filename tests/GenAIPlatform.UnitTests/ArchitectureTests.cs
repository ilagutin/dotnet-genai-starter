using System.Xml.Linq;

namespace GenAIPlatform.UnitTests;

public sealed class ArchitectureTests
{
    private static readonly HashSet<string> ExactReferenceProjects = new(StringComparer.OrdinalIgnoreCase)
    {
        "GenAIPlatform.Domain",
        "GenAIPlatform.Application.Core",
        "GenAIPlatform.Application.Knowledge",
        "GenAIPlatform.Application.Generation",
        "GenAIPlatform.Application.Agentic",
        "GenAIPlatform.Application.Evaluations",
        "GenAIPlatform.Application.Usage"
    };

    private static readonly Dictionary<string, string[]> AllowedReferences = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GenAIPlatform.Domain"] = [],
        ["GenAIPlatform.Application.Core"] = ["GenAIPlatform.Domain"],
        ["GenAIPlatform.Application.Knowledge"] =
            ["GenAIPlatform.Domain", "GenAIPlatform.Application.Core"],
        ["GenAIPlatform.Application.Generation"] =
            ["GenAIPlatform.Domain", "GenAIPlatform.Application.Core", "GenAIPlatform.Application.Knowledge"],
        ["GenAIPlatform.Application.Agentic"] =
            ["GenAIPlatform.Domain", "GenAIPlatform.Application.Core", "GenAIPlatform.Application.Generation"],
        ["GenAIPlatform.Application.Evaluations"] =
            [
                "GenAIPlatform.Domain",
                "GenAIPlatform.Application.Core",
                "GenAIPlatform.Application.Knowledge",
                "GenAIPlatform.Application.Generation"
            ],
        ["GenAIPlatform.Application.Usage"] =
            ["GenAIPlatform.Domain", "GenAIPlatform.Application.Core"],
        ["GenAIPlatform.Infrastructure"] =
            [
                "GenAIPlatform.Domain",
                "GenAIPlatform.Application.Core",
                "GenAIPlatform.Application.Knowledge",
                "GenAIPlatform.Application.Generation",
                "GenAIPlatform.Application.Agentic",
                "GenAIPlatform.Application.Evaluations",
                "GenAIPlatform.Application.Usage"
            ],
        ["GenAIPlatform.Api"] =
            [
                "GenAIPlatform.Domain",
                "GenAIPlatform.Application.Core",
                "GenAIPlatform.Application.Knowledge",
                "GenAIPlatform.Application.Generation",
                "GenAIPlatform.Application.Agentic",
                "GenAIPlatform.Application.Evaluations",
                "GenAIPlatform.Application.Usage",
                "GenAIPlatform.Infrastructure"
            ],
        ["GenAIPlatform.Worker"] =
            [
                "GenAIPlatform.Domain",
                "GenAIPlatform.Application.Core",
                "GenAIPlatform.Application.Knowledge",
                "GenAIPlatform.Application.Usage",
                "GenAIPlatform.Infrastructure"
            ],
        ["GenAIPlatform.Evaluations"] =
            [
                "GenAIPlatform.Domain",
                "GenAIPlatform.Application.Core",
                "GenAIPlatform.Application.Knowledge",
                "GenAIPlatform.Application.Generation",
                "GenAIPlatform.Application.Evaluations",
                "GenAIPlatform.Infrastructure"
            ],
        ["GenAIPlatform.Migrations"] =
            [
                "GenAIPlatform.Infrastructure"
            ],
        ["GenAIPlatform.Mcp"] =
            [
                "GenAIPlatform.Domain",
                "GenAIPlatform.Application.Core",
                "GenAIPlatform.Application.Knowledge",
                "GenAIPlatform.Application.Generation",
                "GenAIPlatform.Application.Agentic",
                "GenAIPlatform.Application.Usage",
                "GenAIPlatform.Infrastructure"
            ]
    };

    [Fact]
    public void SourceProjects_UseOnlyAllowedProjectReferences()
    {
        var projects = LoadSourceProjects();
        var failures = new List<string>();

        foreach (var project in projects.Values.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!AllowedReferences.TryGetValue(project.Name, out var allowedReferences))
            {
                failures.Add($"{project.Name} is not part of the approved source project matrix.");
                continue;
            }

            var allowed = allowedReferences.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in project.ProjectReferences.OrderBy(reference => reference, StringComparer.OrdinalIgnoreCase))
            {
                if (!allowed.Contains(reference))
                {
                    failures.Add($"{project.Name} must not reference {reference}.");
                }
            }

            if (ExactReferenceProjects.Contains(project.Name))
            {
                var expected = allowedReferences.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var actual = project.ProjectReferences.Order(StringComparer.OrdinalIgnoreCase).ToArray();
                if (!expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"{project.Name} references [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}].");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ApplicationModuleNamespaces_MatchTargetModuleOwnership()
    {
        var projects = LoadSourceProjects();
        var rules = ModuleMembershipRules();
        var failures = new List<string>();

        foreach (var rule in rules)
        {
            if (!projects.TryGetValue(rule.ProjectName, out var project))
            {
                continue;
            }

            foreach (var filePath in EnumerateSourceFiles(project.Directory))
            {
                var relativePath = Path.GetRelativePath(project.Directory, filePath).Replace('\\', '/');
                var declaredNamespace = ReadDeclaredNamespace(filePath);
                if (declaredNamespace is null)
                {
                    failures.Add($"{rule.ProjectName}/{relativePath} does not declare a namespace.");
                    continue;
                }

                if (!declaredNamespace.StartsWith(rule.NamespacePrefix, StringComparison.Ordinal))
                {
                    failures.Add(
                        $"{rule.ProjectName}/{relativePath} declares {declaredNamespace}; expected {rule.NamespacePrefix}.");
                }

                var ownershipText = $"{relativePath}/{declaredNamespace}";
                foreach (var forbiddenSegment in rule.ForbiddenSegments)
                {
                    if (ContainsPathOrNamespaceSegment(ownershipText, forbiddenSegment))
                    {
                        failures.Add($"{rule.ProjectName}/{relativePath} contains forbidden segment {forbiddenSegment}.");
                    }
                }

                if (rule.AllowedTopLevelFolders.Count > 0 &&
                    !IsAllowedTopLevelFolder(relativePath, rule.AllowedTopLevelFolders))
                {
                    failures.Add(
                        $"{rule.ProjectName}/{relativePath} is outside allowed folders [{string.Join(", ", rule.AllowedTopLevelFolders)}].");
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void DomainProject_DoesNotDeclareApplicationPorts()
    {
        var domainDirectory = Path.Combine(RepositoryRoot(), "src", "GenAIPlatform.Domain");
        var filesWithInterfaces = EnumerateSourceFiles(domainDirectory)
            .Where(filePath => File.ReadAllText(filePath).Contains("interface ", StringComparison.Ordinal))
            .Select(filePath => Path.GetRelativePath(domainDirectory, filePath).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Empty(filesWithInterfaces);
    }

    [Fact]
    public void ApplicationProjects_DoNotReferenceExternalMcpSdkOrProcessIo()
    {
        var applicationDirectories = Directory
            .EnumerateDirectories(Path.Combine(RepositoryRoot(), "src"), "GenAIPlatform.Application.*")
            .ToArray();
        var forbiddenMarkers = new[]
        {
            "ModelContextProtocol",
            "StdioClientTransport",
            "ProcessStartInfo",
            "System.Diagnostics.Process"
        };
        var failures = new List<string>();

        foreach (var directory in applicationDirectories)
        {
            foreach (var projectPath in Directory.EnumerateFiles(directory, "*.csproj"))
            {
                AddForbiddenMarkers(
                    failures,
                    Path.GetRelativePath(RepositoryRoot(), projectPath),
                    File.ReadAllText(projectPath),
                    forbiddenMarkers);
            }

            foreach (var sourcePath in EnumerateSourceFiles(directory))
            {
                AddForbiddenMarkers(
                    failures,
                    Path.GetRelativePath(RepositoryRoot(), sourcePath),
                    File.ReadAllText(sourcePath),
                    forbiddenMarkers);
            }
        }

        Assert.Empty(failures);
    }

    private static Dictionary<string, SourceProject> LoadSourceProjects()
    {
        var sourceDirectory = Path.Combine(RepositoryRoot(), "src");
        return Directory.EnumerateFiles(sourceDirectory, "*.csproj", SearchOption.AllDirectories)
            .Select(LoadSourceProject)
            .ToDictionary(project => project.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static SourceProject LoadSourceProject(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var references = document
            .Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(include => !string.IsNullOrWhiteSpace(include))
            // ProjectReference Include paths use Windows '\' separators; normalize to '/'
            // so Path APIs resolve them on Linux too (CI runs on ubuntu).
            .Select(include => include!.Replace('\\', '/'))
            .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include)))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SourceProject(Path.GetFileNameWithoutExtension(projectPath), projectDirectory, references);
    }

    private static IReadOnlyList<ModuleMembershipRule> ModuleMembershipRules() =>
        [
            new(
                "GenAIPlatform.Application.Core",
                "GenAIPlatform.Application.Core",
                [],
                ["Chat", "ModelGateway", "Prompts", "Documents", "Agentic", "Evaluations", "Usage", "Observability"]),
            new(
                "GenAIPlatform.Application.Knowledge",
                "GenAIPlatform.Application.Knowledge",
                ["Documents", "Retrieval", "Embeddings"],
                ["Chat", "ModelGateway", "Prompts", "Agentic", "Evaluations", "Usage", "Observability"]),
            new(
                "GenAIPlatform.Application.Generation",
                "GenAIPlatform.Application.Generation",
                ["ModelGateway", "Prompts", "Chat"],
                ["Documents", "Retrieval", "Embeddings", "Agentic", "Evaluations", "Usage", "Observability"]),
            new(
                "GenAIPlatform.Application.Agentic",
                "GenAIPlatform.Application.Agentic",
                ["Chat", "Tools", "Validation"],
                ["Documents", "Retrieval", "Embeddings", "ModelGateway", "Prompts", "Evaluations", "Usage", "Observability"]),
            new(
                "GenAIPlatform.Application.Evaluations",
                "GenAIPlatform.Application.Evaluations",
                [],
                ["Documents", "Retrieval", "Embeddings", "Agentic", "Usage", "Observability"]),
            new(
                "GenAIPlatform.Application.Usage",
                "GenAIPlatform.Application.Usage",
                ["GetUsage"],
                ["Chat", "Documents", "Retrieval", "Embeddings", "ModelGateway", "Prompts", "Agentic", "Evaluations", "Observability"])
        ];

    private static IEnumerable<string> EnumerateSourceFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(filePath => !filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string? ReadDeclaredNamespace(string filePath)
    {
        foreach (var line in File.ReadLines(filePath))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("namespace ", StringComparison.Ordinal))
            {
                continue;
            }

            return trimmed["namespace ".Length..]
                .Trim()
                .TrimEnd(';', '{')
                .Trim();
        }

        return null;
    }

    private static bool ContainsPathOrNamespaceSegment(string value, string segment) =>
        value.Split(['/', '.', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Contains(segment, StringComparer.Ordinal);

    private static bool IsAllowedTopLevelFolder(string relativePath, IReadOnlyCollection<string> allowedFolders)
    {
        var firstSegment = relativePath.Split('/')[0];
        return firstSegment.Equals("Setup.cs", StringComparison.Ordinal) ||
            allowedFolders.Contains(firstSegment);
    }

    private static void AddForbiddenMarkers(
        List<string> failures,
        string relativePath,
        string content,
        IReadOnlyCollection<string> markers)
    {
        foreach (var marker in markers)
        {
            if (content.Contains(marker, StringComparison.Ordinal))
            {
                failures.Add($"{relativePath} contains forbidden external MCP/I/O marker {marker}.");
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GenAIPlatform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate GenAIPlatform.slnx.");
    }

    private sealed record SourceProject(string Name, string Directory, string[] ProjectReferences);

    private sealed record ModuleMembershipRule(
        string ProjectName,
        string NamespacePrefix,
        IReadOnlyCollection<string> AllowedTopLevelFolders,
        IReadOnlyCollection<string> ForbiddenSegments);
}
