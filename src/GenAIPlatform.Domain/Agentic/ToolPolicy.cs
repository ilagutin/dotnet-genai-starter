using System.Text;

namespace GenAIPlatform.Domain.Agentic;

public sealed class ToolPolicy
{
    // Keeps audit classification explicit for well-known destructive names even
    // when no matching tool is registered in the demo registry. The check runs
    // before tool metadata so external wrappers cannot downgrade a forbidden
    // final tool name to approval-only metadata.
    private static readonly string[] KnownForbiddenToolNameFragments =
    [
        "sendemail",
        "deletedocument",
        "runsqlquery"
    ];

    public ToolPolicyDecision Decide(ToolPolicyMetadata? policy, string requestedToolName)
    {
        if (IsKnownForbiddenToolName(requestedToolName))
        {
            return new ToolPolicyDecision(
                ToolRisk.Forbidden,
                "Forbidden",
                "The backend policy forbids this tool for model-proposed execution.",
                RequiresApproval: false,
                MayExecute: false);
        }

        if (policy is not null)
        {
            return policy.ToDecision();
        }

        return new ToolPolicyDecision(
            ToolRisk.Forbidden,
            "UnknownTool",
            "The requested tool is not registered for this user/request.",
            RequiresApproval: false,
            MayExecute: false);
    }

    private static bool IsKnownForbiddenToolName(string requestedToolName)
    {
        var normalized = NormalizeToolName(requestedToolName);
        return KnownForbiddenToolNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.Ordinal));
    }

    private static string NormalizeToolName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                continue;
            }

            if (character is >= 'A' and <= 'Z')
            {
                builder.Append((char)(character + ('a' - 'A')));
            }
        }

        return builder.ToString();
    }
}
