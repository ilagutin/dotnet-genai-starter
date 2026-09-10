using System.Security.Cryptography;
using System.Text;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline.Corpus;

/// <summary>
/// Derives stable identifiers for benchmark rows from the dataset's readable ids, so a
/// baseline run replaces the same rows every time and a report can be traced back to the
/// dataset without storing a mapping table. The digest is an identifier derivation, not a
/// security control.
/// </summary>
public static class RetrievalBaselineIdFactory
{
    private const string DocumentNamespace = "genai.retrieval-baseline.document";
    private const string ChunkNamespace = "genai.retrieval-baseline.chunk";

    public static Guid CreateDocumentId(string documentId)
    {
        return CreateId(DocumentNamespace, documentId);
    }

    public static Guid CreateChunkId(string chunkId)
    {
        return CreateId(ChunkNamespace, chunkId);
    }

    private static Guid CreateId(string idNamespace, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{idNamespace}:{value}"));
        var bytes = digest.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}
