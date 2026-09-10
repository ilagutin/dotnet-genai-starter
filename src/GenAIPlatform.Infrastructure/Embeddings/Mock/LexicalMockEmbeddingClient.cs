using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Embeddings;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Embeddings.Mock;

/// <summary>
/// A deterministic mock embedding adapter whose vectors carry token overlap instead of
/// content-hash noise, so retrieval plumbing can be exercised end to end without a real
/// provider. Signed feature hashing places each token at a stable index and sign, and the
/// L2-normalized result makes cosine similarity a token-overlap measure. This is an
/// engineering fixture: it models no semantics and must not be read as answer quality.
/// </summary>
internal sealed class LexicalMockEmbeddingClient(IOptions<EmbeddingOptions> options) : IEmbeddingClient
{
    public const string ProviderName = "mock-lexical";
    private const int MinimumTokenLength = 2;

    public Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var dimensions = Math.Clamp(options.Value.MockDimensions, 1, 4096);
        var tokens = Tokenize(Truncate(request.Input, options.Value.MaxInputCharacters));

        return Task.FromResult(new EmbeddingResponse(
            CreateVector(tokens, dimensions),
            request.Model,
            ProviderName,
            tokens.Count,
            request.CorrelationId));
    }

    private static string Truncate(string input, int maxInputCharacters)
    {
        var limit = Math.Max(1, maxInputCharacters);
        return input.Length <= limit ? input : input[..limit];
    }

    private static IReadOnlyList<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        foreach (var character in input)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                continue;
            }

            AppendToken(tokens, builder);
        }

        AppendToken(tokens, builder);
        return tokens;
    }

    private static void AppendToken(List<string> tokens, StringBuilder builder)
    {
        if (builder.Length >= MinimumTokenLength)
        {
            tokens.Add(builder.ToString());
        }

        builder.Clear();
    }

    private static float[] CreateVector(IReadOnlyList<string> tokens, int dimensions)
    {
        var vector = new float[dimensions];

        // An input without usable tokens still has to produce a valid nonzero cosine
        // vector, so it falls back to the stable feature of the empty token.
        IReadOnlyList<string> features = tokens.Count == 0 ? [string.Empty] : tokens;
        foreach (var token in features)
        {
            AddFeature(vector, token, dimensions);
        }

        return Normalize(vector);
    }

    private static void AddFeature(float[] vector, string token, int dimensions)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var index = (int)(BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(0, 4)) % (uint)dimensions);
        vector[index] += (hash[4] & 1) == 0 ? 1f : -1f;
    }

    private static float[] Normalize(float[] vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(static value => (double)value * value));
        if (magnitude <= 0)
        {
            // Opposite-sign collisions can cancel a whole vector. Fall back to a fixed
            // unit vector so the embedding contract still holds.
            vector[0] = 1f;
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / magnitude);
        }

        return vector;
    }
}
