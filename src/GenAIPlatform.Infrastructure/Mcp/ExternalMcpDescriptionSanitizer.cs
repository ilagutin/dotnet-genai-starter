using System.Text;

namespace GenAIPlatform.Infrastructure.Mcp;

internal static class ExternalMcpDescriptionSanitizer
{
    public const int MaxLength = 512;

    public static string Sanitize(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return "External MCP tool. Requires backend approval before execution.";
        }

        var builder = new StringBuilder(Math.Min(description.Length, MaxLength));
        var lastWasWhitespace = false;

        foreach (var character in description)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                if (!lastWasWhitespace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasWhitespace = true;
                }

                continue;
            }

            builder.Append(character);
            lastWasWhitespace = false;

            if (builder.Length >= MaxLength)
            {
                break;
            }
        }

        var sanitized = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? "External MCP tool. Requires backend approval before execution."
            : sanitized;
    }
}
