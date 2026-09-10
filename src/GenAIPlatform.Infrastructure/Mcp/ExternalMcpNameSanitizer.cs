using System.Security.Cryptography;
using System.Text;

namespace GenAIPlatform.Infrastructure.Mcp;

internal static class ExternalMcpNameSanitizer
{
    public const int MaxToolNameLength = 64;
    public const int MaxLogIdentityLength = 64;

    private const int HashSuffixLength = 10;

    public static string BuildPrefixedToolName(string serverName, string toolName)
    {
        var name = $"mcp_{SanitizeSegment(serverName, "server")}_{SanitizeSegment(toolName, "tool")}";
        return ShortenIfNeeded(name, MaxToolNameLength, "mcp");
    }

    public static string SanitizeLogIdentity(string serverName)
    {
        var sanitized = SanitizeSegment(serverName, "server");
        return ShortenIfNeeded(sanitized, MaxLogIdentityLength, "server");
    }

    public static string SanitizeSegment(string value, string fallback)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSeparator = false;

        foreach (var character in value.Trim())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(character);
                lastWasSeparator = false;
                continue;
            }

            if (character is >= 'A' and <= 'Z')
            {
                builder.Append((char)(character + ('a' - 'A')));
                lastWasSeparator = false;
                continue;
            }

            if (!lastWasSeparator)
            {
                builder.Append('_');
                lastWasSeparator = true;
            }
        }

        var sanitized = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private static string ShortenIfNeeded(string name, int maxLength, string fallback)
    {
        if (name.Length <= maxLength)
        {
            return name;
        }

        var suffix = $"_{ShortHash(name)}";
        var prefixLength = maxLength - suffix.Length;
        var prefix = name[..prefixLength].TrimEnd('_');
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = fallback;
        }

        if (prefix.Length + suffix.Length > maxLength)
        {
            prefix = prefix[..(maxLength - suffix.Length)];
        }

        return prefix + suffix;
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..HashSuffixLength].ToLowerInvariant();
    }
}
