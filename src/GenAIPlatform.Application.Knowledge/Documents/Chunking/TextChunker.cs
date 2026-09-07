using System.Security.Cryptography;
using System.Text;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents;

public sealed class TextChunker(IOptions<DocumentIngestionOptions> options) : ITextChunker
{
    public IReadOnlyList<DocumentChunk> Chunk(
        Document document,
        string text,
        DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var currentOptions = options.Value;
        var maxCharacters = currentOptions.ChunkMaxCharacters;
        if (maxCharacters <= 0)
        {
            throw new InvalidOperationException(
                "Document chunk max characters must be greater than zero.");
        }

        var overlapCharacters = Math.Clamp(
            currentOptions.ChunkOverlapCharacters,
            0,
            maxCharacters / 2);
        var normalizedText = NormalizeText(text);
        var chunks = new List<DocumentChunk>();
        var start = 0;

        while (start < normalizedText.Length)
        {
            var end = FindChunkEnd(normalizedText, start, maxCharacters);
            var chunkText = normalizedText[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunkText))
            {
                var position = chunks.Count;
                var textHash = ComputeHash(chunkText);
                chunks.Add(new DocumentChunk(
                    CreateStableChunkId(document.Id, document.Version, position, textHash),
                    document.Id,
                    document.Version,
                    position,
                    chunkText,
                    textHash,
                    CountApproximateTokens(chunkText),
                    currentOptions.ChunkingProfile,
                    currentOptions.ChunkingProfileVersion,
                    [],
                    string.Empty,
                    string.Empty,
                    null,
                    createdAtUtc));
            }

            if (end >= normalizedText.Length)
            {
                break;
            }

            var nextStart = Math.Max(end - overlapCharacters, start + 1);
            while (nextStart < normalizedText.Length &&
                   char.IsWhiteSpace(normalizedText[nextStart]))
            {
                nextStart++;
            }

            start = nextStart <= start ? end : nextStart;
        }

        return chunks;
    }

    private static int FindChunkEnd(string text, int start, int maxCharacters)
    {
        var hardEnd = Math.Min(start + maxCharacters, text.Length);
        if (hardEnd >= text.Length)
        {
            return text.Length;
        }

        var minSoftEnd = start + Math.Min(200, maxCharacters / 2);
        for (var i = hardEnd - 1; i > minSoftEnd; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                return i;
            }
        }

        return hardEnd;
    }

    private static string NormalizeText(string text)
    {
        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static int CountApproximateTokens(string value)
    {
        return value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Guid CreateStableChunkId(
        Guid documentId,
        int documentVersion,
        int position,
        string textHash)
    {
        var input = $"{documentId:n}:{documentVersion}:{position}:{textHash}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(bytes[..16]);
    }
}
