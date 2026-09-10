using System.Security.Cryptography;
using System.Text;

namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// SHA-256 over canonicalized migration SQL. Canonicalization strips a UTF-8 byte order mark and
/// normalizes CRLF/CR to LF so the same script checksums identically on Windows and Linux,
/// whatever the checkout applied to line endings.
/// </summary>
internal static class MigrationChecksum
{
    private const char ByteOrderMark = (char)0xFEFF;

    public static string Compute(string sql)
    {
        var canonical = Canonicalize(sql);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Canonicalize(string sql)
    {
        return sql
            .TrimStart(ByteOrderMark)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }
}
