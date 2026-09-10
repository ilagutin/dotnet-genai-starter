using System.Text;

namespace GenAIPlatform.Application.Generation.Chat;

/// <summary>
/// Builds the backend-owned frame that labels retrieved document text as data inside a prompt.
/// The frame is a formatting boundary only; it is not a security boundary and does not make the
/// model immune to instructions embedded in document text.
/// </summary>
internal static class RetrievedSourceFrame
{
    public const string CloseTag = "</source>";

    private const int MaxPromptMetadataCharacters = 120;
    private const string OpenMarker = "source";
    private const string CloseMarker = "/source";

    public static int Overhead(string openTag, string separator)
    {
        return separator.Length
            + openTag.Length
            + Environment.NewLine.Length
            + Environment.NewLine.Length
            + CloseTag.Length;
    }

    public static string BuildOpenTag(string referenceId, string title, string fileName)
    {
        return new StringBuilder()
            .Append("<source id=\"")
            .Append(EscapeAttribute(referenceId))
            .Append("\" title=\"")
            .Append(EscapeAttribute(NormalizePromptMetadata(title)))
            .Append("\" file=\"")
            .Append(EscapeAttribute(NormalizePromptMetadata(fileName)))
            .Append("\">")
            .ToString();
    }

    /// <summary>
    /// Escapes an attribute value so that it can never terminate the frame tag.
    /// Order matters: ampersands are escaped first so later replacements are not double escaped.
    /// </summary>
    public static string EscapeAttribute(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    /// <summary>
    /// Rewrites every case-insensitive framing marker inside document text by inserting a
    /// backslash right after the opening angle bracket, so document text cannot forge a frame.
    /// Nothing else is removed or rewritten.
    /// </summary>
    public static string NeutralizeFramingMarkers(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        StringBuilder? builder = null;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '<' && IsFramingMarkerAt(text, index + 1))
            {
                builder ??= new StringBuilder(text.Length + 8).Append(text, 0, index);
                builder.Append("<\\");
                continue;
            }

            builder?.Append(character);
        }

        return builder?.ToString() ?? text;
    }

    public static string NormalizePromptMetadata(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            ' ',
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= MaxPromptMetadataCharacters
            ? normalized
            : normalized[..MaxPromptMetadataCharacters].TrimEnd();
    }

    private static bool IsFramingMarkerAt(string text, int index)
    {
        return StartsWithMarker(text, index, OpenMarker)
            || StartsWithMarker(text, index, CloseMarker);
    }

    private static bool StartsWithMarker(string text, int index, string marker)
    {
        if (index + marker.Length > text.Length)
        {
            return false;
        }

        return text.AsSpan(index, marker.Length).Equals(marker, StringComparison.OrdinalIgnoreCase);
    }
}
