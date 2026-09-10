using System.Security.Cryptography;
using System.Text.Json;
using GenAIPlatform.Domain.Evaluations.Retrieval;
using GenAIPlatform.Domain.Exceptions;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Loads the frozen retrieval baseline dataset from an embedded resource and hashes its
/// canonical bytes, so a report identifies the dataset it measured without restating any
/// of its text. The seed is a text resource under `* text=auto`, so the same commit is
/// checked out with LF on Linux and CRLF on Windows; the digest is taken after stripping a
/// UTF-8 byte order mark and folding every line ending to LF, which makes it a property of
/// the dataset content rather than of the checkout that produced the assembly.
/// </summary>
public sealed class InMemoryRetrievalBaselineDatasetProvider : IRetrievalBaselineDatasetProvider
{
    public const string ResourceName =
        "GenAIPlatform.Application.Evaluations.Seeds.retrieval-baseline.v1.json";

    private const byte CarriageReturn = (byte)'\r';
    private const byte LineFeed = (byte)'\n';

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly byte[] Utf8ByteOrderMark = [0xEF, 0xBB, 0xBF];

    public async Task<RetrievalBaselineDatasetSource> GetDatasetAsync(
        string? datasetVersion,
        CancellationToken cancellationToken)
    {
        var bytes = Canonicalize(await ReadResourceBytesAsync(cancellationToken));
        var dataset = JsonSerializer.Deserialize<RetrievalBaselineDataset>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("Embedded retrieval baseline dataset could not be read.");

        if (!string.IsNullOrWhiteSpace(datasetVersion) &&
            !string.Equals(dataset.Version, datasetVersion.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new EvaluationValidationException(
                $"Retrieval baseline dataset version '{datasetVersion}' was not found.");
        }

        return new RetrievalBaselineDatasetSource(
            dataset,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>
    /// Strips a UTF-8 byte order mark and folds CRLF and lone CR to LF, so a Windows and a
    /// Linux checkout of the same seed produce the same digest.
    /// </summary>
    public static byte[] Canonicalize(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var source = bytes.AsSpan();
        if (source.StartsWith(Utf8ByteOrderMark))
        {
            source = source[Utf8ByteOrderMark.Length..];
        }

        var canonical = new byte[source.Length];
        var length = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var value = source[index];
            if (value == CarriageReturn)
            {
                canonical[length++] = LineFeed;
                if (index + 1 < source.Length && source[index + 1] == LineFeed)
                {
                    index++;
                }

                continue;
            }

            canonical[length++] = value;
        }

        return canonical[..length];
    }

    private static async Task<byte[]> ReadResourceBytesAsync(CancellationToken cancellationToken)
    {
        await using var stream = typeof(InMemoryRetrievalBaselineDatasetProvider).Assembly
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded retrieval baseline dataset was not found.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);

        return buffer.ToArray();
    }
}
