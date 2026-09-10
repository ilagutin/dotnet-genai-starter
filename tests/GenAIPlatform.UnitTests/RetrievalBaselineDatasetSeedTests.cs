using System.Security.Cryptography;
using System.Text;
using GenAIPlatform.Application.Evaluations.RetrievalBaseline;
using GenAIPlatform.Domain.Evaluations.Retrieval;
using GenAIPlatform.Domain.Exceptions;

namespace GenAIPlatform.UnitTests;

public sealed class RetrievalBaselineDatasetSeedTests
{
    /// <summary>
    /// The digest the committed report `docs/evaluations/retrieval-baseline-v1.json`
    /// records. Changing the seed must change this constant in the same commit, otherwise
    /// the committed artifact silently stops describing the dataset it claims to measure.
    /// </summary>
    private const string PinnedDatasetHash =
        "2c567f8bccf50bb23a09f4bc90c11e24bb98d3e1cab4fdb162416ffbf36f863b";

    private static readonly IReadOnlyDictionary<string, int> DocumentedQueryCountsByCategory =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [RetrievalBaselineCategories.Relevant] = 8,
            [RetrievalBaselineCategories.Distractor] = 4,
            [RetrievalBaselineCategories.Paraphrase] = 5,
            [RetrievalBaselineCategories.NoMatch] = 5,
            [RetrievalBaselineCategories.TenantIsolation] = 3,
            [RetrievalBaselineCategories.PrivateOwnership] = 3,
            [RetrievalBaselineCategories.DocumentVersion] = 3,
            [RetrievalBaselineCategories.EmbeddingCompatibility] = 3
        };

    [Fact]
    public async Task EmbeddedDatasetPassesValidation()
    {
        var source = await LoadAsync();

        Assert.Same(source.Dataset, new RetrievalBaselineDatasetValidator().Validate(source.Dataset));
        Assert.Equal("retrieval-baseline-v1", source.Dataset.Version);
        Assert.Equal("retrieval-baseline", source.Dataset.TenantPrefix);
    }

    [Fact]
    public async Task EmbeddedDatasetHashMatchesTheDigestTheCommittedReportRecords()
    {
        var first = await LoadAsync();
        var second = await LoadAsync();

        Assert.Equal(PinnedDatasetHash, first.ContentHash);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Matches("^[0-9a-f]{64}$", first.ContentHash);
    }

    [Fact]
    public void DatasetHashIsTheSameWhicheverLineEndingsTheCheckoutProduced()
    {
        var lineFeedBytes = ToLineFeed(ReadSeedBytes());
        var carriageReturnBytes = ToCarriageReturnLineFeed(lineFeedBytes);
        var byteOrderMarkedBytes = WithByteOrderMark(carriageReturnBytes);

        Assert.NotEqual(Digest(lineFeedBytes), Digest(carriageReturnBytes));
        Assert.Equal(
            Digest(InMemoryRetrievalBaselineDatasetProvider.Canonicalize(lineFeedBytes)),
            Digest(InMemoryRetrievalBaselineDatasetProvider.Canonicalize(carriageReturnBytes)));
        Assert.Equal(
            PinnedDatasetHash,
            Digest(InMemoryRetrievalBaselineDatasetProvider.Canonicalize(carriageReturnBytes)));
        Assert.Equal(
            PinnedDatasetHash,
            Digest(InMemoryRetrievalBaselineDatasetProvider.Canonicalize(byteOrderMarkedBytes)));
    }

    [Fact]
    public async Task EmbeddedDatasetCoversEveryLabeledCategoryWithAtLeastThirtyQueries()
    {
        var dataset = (await LoadAsync()).Dataset;

        Assert.True(dataset.Queries.Count >= 30, $"query count was {dataset.Queries.Count}");
        foreach (var category in RetrievalBaselineCategories.All)
        {
            Assert.Contains(dataset.Queries, query => query.Category == category);
        }
    }

    [Fact]
    public async Task EmbeddedDatasetMatchesTheShapeTheDocumentationStates()
    {
        var dataset = (await LoadAsync()).Dataset;

        Assert.Equal(19, dataset.Corpus.Count);
        Assert.Equal(21, dataset.Corpus.Sum(document => document.Chunks.Count));
        Assert.Equal(34, dataset.Queries.Count);
        Assert.Equal(
            DocumentedQueryCountsByCategory.OrderBy(entry => entry.Key, StringComparer.Ordinal),
            dataset.Queries
                .GroupBy(query => query.Category, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal)
                .OrderBy(entry => entry.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task EmbeddedDatasetCarriesTheFixturesEveryPermissionRuleNeeds()
    {
        var dataset = (await LoadAsync()).Dataset;

        Assert.Contains(dataset.Corpus, document => document.TenantId != dataset.TenantPrefix);
        Assert.Contains(dataset.Corpus, document => document.AccessLevel == "Private");
        Assert.Contains(dataset.Corpus, document => document.EmbeddingProvider == "mock-other");
        Assert.Contains(
            dataset.Corpus,
            document => document.Chunks.Any(chunk => chunk.DocumentVersion is not null &&
                                                     chunk.DocumentVersion < document.Version));
    }

    [Fact]
    public async Task EmbeddedDatasetCarriesADocumentThatOnlyItsEmbeddingModelMakesIneligible()
    {
        var dataset = (await LoadAsync()).Dataset;

        var modelOnlyMismatch = Assert.Single(
            dataset.Corpus,
            document => document.EmbeddingModel is not null && document.EmbeddingProvider is null);
        Assert.Equal("mock-embedding-legacy", modelOnlyMismatch.EmbeddingModel);
        Assert.Contains(
            dataset.Queries,
            query => query.Category == RetrievalBaselineCategories.EmbeddingCompatibility &&
                     query.NoMatch &&
                     query.Id == "q-embedding-compatibility-003");
        Assert.DoesNotContain(
            dataset.Queries,
            query => (query.ExpectedDocumentIds ?? []).Contains(modelOnlyMismatch.Id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task EmbeddedDatasetFreezesTheGatesLaterRetrievalWorkMustKeep()
    {
        var gates = (await LoadAsync()).Dataset.Gates;

        Assert.Equal(3, gates.K);
        Assert.Equal(0.9, gates.MinRecallAtK);
        Assert.Equal(1, gates.MinNoMatchAccuracy);
    }

    [Fact]
    public async Task UnknownDatasetVersionIsRejected()
    {
        var provider = new InMemoryRetrievalBaselineDatasetProvider();

        await Assert.ThrowsAsync<EvaluationValidationException>(
            () => provider.GetDatasetAsync("retrieval-baseline-v9", TestContext.Current.CancellationToken));
    }

    private static Task<RetrievalBaselineDatasetSource> LoadAsync()
    {
        return new InMemoryRetrievalBaselineDatasetProvider()
            .GetDatasetAsync(null, TestContext.Current.CancellationToken);
    }

    private static byte[] ReadSeedBytes()
    {
        using var stream = typeof(InMemoryRetrievalBaselineDatasetProvider).Assembly
            .GetManifestResourceStream(InMemoryRetrievalBaselineDatasetProvider.ResourceName);
        Assert.NotNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        return buffer.ToArray();
    }

    private static byte[] ToCarriageReturnLineFeed(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\n", "\r\n", StringComparison.Ordinal);

        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Folds CRLF and lone CR to LF, independently of the production canonicalizer, so this
    /// test does not assume the checkout that produced the seed on disk was LF to begin with.
    /// </summary>
    private static byte[] ToLineFeed(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);

        return Encoding.UTF8.GetBytes(text);
    }

    private static byte[] WithByteOrderMark(byte[] bytes)
    {
        return [.. Encoding.UTF8.GetPreamble(), .. bytes];
    }

    private static string Digest(byte[] bytes)
    {
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
