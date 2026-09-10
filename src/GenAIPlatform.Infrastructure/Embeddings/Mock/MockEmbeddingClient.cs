using System.Security.Cryptography;
using System.Text;
using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Embeddings;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Infrastructure.Embeddings.Mock;

internal sealed class MockEmbeddingClient(IOptions<EmbeddingOptions> options) : IEmbeddingClient
{
    public Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var dimensions = Math.Clamp(options.Value.MockDimensions, 1, 4096);
        var vector = CreateDeterministicVector(request.Input, dimensions);

        return Task.FromResult(new EmbeddingResponse(
            vector,
            request.Model,
            "mock",
            CountApproximateTokens(request.Input),
            request.CorrelationId));
    }

    private static float[] CreateDeterministicVector(string input, int dimensions)
    {
        var vector = new float[dimensions];
        var seed = Encoding.UTF8.GetBytes(input);

        for (var offset = 0; offset < dimensions; offset += 32)
        {
            var hashInput = seed
                .Concat(BitConverter.GetBytes(offset))
                .ToArray();
            var hash = SHA256.HashData(hashInput);

            for (var i = 0; i < hash.Length && offset + i < dimensions; i++)
            {
                vector[offset + i] = ((hash[i] / 255f) * 2f) - 1f;
            }
        }

        return Normalize(vector);
    }

    private static float[] Normalize(float[] vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(static value => value * value));
        if (magnitude <= 0)
        {
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / magnitude);
        }

        return vector;
    }

    private static int CountApproximateTokens(string value)
    {
        return value
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }
}
