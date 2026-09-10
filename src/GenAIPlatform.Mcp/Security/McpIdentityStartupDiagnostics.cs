using System.Globalization;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Mcp.Security;

/// <summary>
/// Warns once at startup when the configured local MCP service identity carries the
/// privileged <c>admin</c> role, since the stdio host trusts its configuration file as the
/// caller identity for every connected client.
/// </summary>
internal sealed class McpIdentityStartupDiagnostics(
    IOptions<McpIdentityOptions> options,
    ILogger<McpIdentityStartupDiagnostics> logger) : IHostedService
{
    private const int MaxIdentifierLength = 64;
    private const string AdminRole = "admin";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var identity = options.Value;
        var roles = DistinctRoles(identity.Roles);

        if (roles.Contains(AdminRole, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "The configured local MCP identity (UserId={UserId}, TenantId={TenantId}) has the " +
                "privileged 'admin' role, which grants cross-tenant usage reads. The stdio host " +
                "trusts its configuration file as the caller identity for every connected client " +
                "and is intended for local single-user use only. Remote or multi-user use requires " +
                "per-caller authentication.",
                BoundIdentifier(identity.UserId),
                BoundIdentifier(identity.TenantId));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static IReadOnlyCollection<string> DistinctRoles(IEnumerable<string> roles) =>
        roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Sanitizes an identity value for inclusion in a log message: strips characters that could
    /// forge additional log lines or otherwise manipulate log layout (control, format, line
    /// separator and paragraph separator code points), then truncates to
    /// <see cref="MaxIdentifierLength"/> UTF-16 code units without splitting a surrogate pair.
    /// </summary>
    private static string BoundIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var withoutUnprintableCharacters = RemoveUnprintableCharacters(value);
        return Truncate(withoutUnprintableCharacters, MaxIdentifierLength);
    }

    private static string RemoveUnprintableCharacters(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var rune in value.EnumerateRunes())
        {
            if (IsUnprintable(rune))
            {
                continue;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    private static bool IsUnprintable(Rune rune)
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category
            is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cutLength = maxLength;
        if (cutLength > 0 && char.IsHighSurrogate(value[cutLength - 1]))
        {
            cutLength--;
        }

        return value[..cutLength];
    }
}
