using System.Globalization;
using System.Text;
using GenAIPlatform.Application.Knowledge.Retrieval;

namespace GenAIPlatform.Application.Generation.Chat;

public sealed class RagPromptBuilder
{
    public RagPromptContext Build(
        IReadOnlyList<RetrievedDocumentChunk> chunks,
        int maxContextCharacters)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxContextCharacters);

        var context = new StringBuilder();
        var citations = new List<RagCitation>(chunks.Count);

        foreach (var chunk in chunks)
        {
            var referenceId = (citations.Count + 1).ToString(CultureInfo.InvariantCulture);
            var text = RetrievedSourceFrame.NeutralizeFramingMarkers(chunk.Text.Trim());
            var separator = context.Length > 0 ? Environment.NewLine + Environment.NewLine : string.Empty;
            var openTag = RetrievedSourceFrame.BuildOpenTag(referenceId, chunk.Title, chunk.FileName);
            var remainingTextCharacters = maxContextCharacters
                - context.Length
                - RetrievedSourceFrame.Overhead(openTag, separator);

            if (remainingTextCharacters <= 0)
            {
                continue;
            }

            if (text.Length > remainingTextCharacters)
            {
                text = TruncateWithoutSplittingSurrogatePair(text, remainingTextCharacters);
            }

            if (text.Length == 0)
            {
                continue;
            }

            context
                .Append(separator)
                .Append(openTag)
                .Append(Environment.NewLine)
                .Append(text)
                .Append(Environment.NewLine)
                .Append(RetrievedSourceFrame.CloseTag);

            citations.Add(new RagCitation(
                referenceId,
                chunk.DocumentId,
                chunk.ChunkId,
                chunk.DocumentVersion,
                chunk.ChunkPosition,
                chunk.Title,
                chunk.FileName,
                chunk.SimilarityScore));
        }

        return new RagPromptContext(context.ToString(), citations);
    }

    /// <summary>
    /// Cuts text to at most <paramref name="maxCharacters" /> UTF-16 code units and then trims
    /// trailing whitespace. When the cut would land between the two halves of a surrogate pair it
    /// backs off by one code unit, so a lone high surrogate is never framed into the prompt. The
    /// result is never longer than the requested bound, so the budget arithmetic still holds.
    /// </summary>
    private static string TruncateWithoutSplittingSurrogatePair(string text, int maxCharacters)
    {
        var cut = maxCharacters;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut].TrimEnd();
    }
}
