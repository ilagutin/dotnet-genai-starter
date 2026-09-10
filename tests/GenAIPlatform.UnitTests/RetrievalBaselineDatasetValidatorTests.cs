using GenAIPlatform.Domain.Evaluations.Retrieval;
using GenAIPlatform.Domain.Exceptions;

namespace GenAIPlatform.UnitTests;

public sealed class RetrievalBaselineDatasetValidatorTests
{
    private const string TenantPrefix = "retrieval-baseline";

    [Fact]
    public void Validate_AcceptsAWellFormedDataset()
    {
        var dataset = Dataset();

        Assert.Same(dataset, new RetrievalBaselineDatasetValidator().Validate(dataset));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RejectsABlankVersion(string version)
    {
        AssertRejects(Dataset() with { Version = version }, "version is required");
    }

    [Fact]
    public void Validate_RejectsABlankTenantPrefix()
    {
        AssertRejects(Dataset() with { TenantPrefix = " " }, "tenant prefix is required");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public void Validate_RejectsRetrievalDepthOutsideTheSupportedRange(int k)
    {
        AssertRejects(WithGates(new RetrievalBaselineGates(0.9, 1, k)), "gate k must be between");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Validate_RejectsRecallGatesOutsideZeroToOne(double minRecall)
    {
        AssertRejects(WithGates(new RetrievalBaselineGates(minRecall, 1, 3)), "minRecallAtK must be between");
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void Validate_RejectsNoMatchGatesOutsideZeroToOne(double minNoMatch)
    {
        AssertRejects(
            WithGates(new RetrievalBaselineGates(0.9, minNoMatch, 3)),
            "minNoMatchAccuracy must be between");
    }

    [Fact]
    public void Validate_RejectsDuplicateDocumentIds()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with { Corpus = [dataset.Corpus[0], dataset.Corpus[0]] },
            "duplicate document id");
    }

    [Fact]
    public void Validate_RejectsDuplicateChunkIds()
    {
        var dataset = Dataset();
        var duplicate = dataset.Corpus[1] with { Chunks = dataset.Corpus[0].Chunks };
        AssertRejects(dataset with { Corpus = [dataset.Corpus[0], duplicate] }, "duplicate chunk id");
    }

    [Fact]
    public void Validate_RejectsDuplicateQueryIds()
    {
        var dataset = Dataset();
        AssertRejects(dataset with { Queries = [dataset.Queries[0], dataset.Queries[0]] }, "duplicate query id");
    }

    [Fact]
    public void Validate_RejectsATenantOutsideTheBenchmarkPrefix()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with { Corpus = [dataset.Corpus[0] with { TenantId = "production" }, dataset.Corpus[1]] },
            "outside the benchmark tenant prefix");
    }

    [Fact]
    public void Validate_RejectsAQueryTenantOutsideTheBenchmarkPrefix()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with { Queries = [dataset.Queries[0] with { TenantId = "production" }] },
            "outside the benchmark tenant prefix");
    }

    [Fact]
    public void Validate_RejectsAnUnknownAccessLevel()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with { Corpus = [dataset.Corpus[0] with { AccessLevel = "Public" }, dataset.Corpus[1]] },
            "unknown access level");
    }

    [Fact]
    public void Validate_RejectsADocumentWithoutCurrentVersionChunks()
    {
        var dataset = Dataset();
        var historyOnly = dataset.Corpus[0] with
        {
            Version = 2,
            Chunks = [dataset.Corpus[0].Chunks[0] with { DocumentVersion = 1 }]
        };
        AssertRejects(
            dataset with { Corpus = [historyOnly, dataset.Corpus[1]] },
            "at least one chunk at its current version");
    }

    [Fact]
    public void Validate_RejectsAChunkVersionAboveTheDocumentVersion()
    {
        var dataset = Dataset();
        var invalid = dataset.Corpus[0] with
        {
            Chunks = [dataset.Corpus[0].Chunks[0] with { DocumentVersion = 4 }]
        };
        AssertRejects(dataset with { Corpus = [invalid, dataset.Corpus[1]] }, "document version outside");
    }

    [Fact]
    public void Validate_RejectsAnUnknownCategory()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with { Queries = [dataset.Queries[0] with { Category = "vibes" }] },
            "unknown category");
    }

    [Fact]
    public void Validate_RejectsABlankQuestion()
    {
        var dataset = Dataset();
        AssertRejects(dataset with { Queries = [dataset.Queries[0] with { Question = "  " }] }, "must define a question");
    }

    [Fact]
    public void Validate_RejectsANoMatchQueryThatExpectsDocuments()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with { Queries = [dataset.Queries[0] with { NoMatch = true }] },
            "labeled no-match but expects documents");
    }

    [Fact]
    public void Validate_RejectsExpectedDocumentsMissingFromTheCorpus()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with { Queries = [dataset.Queries[0] with { ExpectedDocumentIds = ["doc-missing"] }] },
            "which the corpus does not contain");
    }

    [Fact]
    public void Validate_RejectsExpectedDocumentsInAnotherTenant()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with
            {
                Queries = [dataset.Queries[0] with { TenantId = $"{TenantPrefix}-other" }]
            },
            "which its caller cannot read");
    }

    [Fact]
    public void Validate_RejectsExpectedPrivateDocumentsOwnedByAnotherUser()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with
            {
                Queries = [dataset.Queries[0] with { ExpectedDocumentIds = ["doc-private"] }]
            },
            "which its caller cannot read");
    }

    [Fact]
    public void Validate_RejectsExpectedDocumentsEmbeddedWithAnIncompatibleProvider()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with
            {
                Queries = [dataset.Queries[0] with { ExpectedDocumentIds = ["doc-incompatible"] }]
            },
            "embedded with an incompatible provider");
    }

    [Fact]
    public void Validate_RejectsExpectedChunksThatAreNotCurrentVersionChunksOfExpectedDocuments()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with
            {
                Queries = [dataset.Queries[0] with { ExpectedChunkIds = ["chunk-b-1"] }]
            },
            "not a current-version chunk of an expected document");
    }

    [Fact]
    public void Validate_RejectsAnEmptyCorpus()
    {
        AssertRejects(Dataset() with { Corpus = [] }, "at least one corpus document");
    }

    [Fact]
    public void Validate_RejectsAnEmptyQuerySet()
    {
        AssertRejects(Dataset() with { Queries = [] }, "at least one query");
    }

    [Fact]
    public void Validate_RejectsAQueryThatExpectsNoDocumentsWithoutBeingLabeledNoMatch()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with { Queries = [dataset.Queries[0] with { ExpectedDocumentIds = [], ExpectedChunkIds = null }] },
            "expects no documents but is not labeled no-match");
    }

    [Fact]
    public void Validate_RejectsANoMatchCategoryQueryThatIsNotLabeledNoMatch()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with
            {
                Queries =
                [
                    dataset.Queries[0] with
                    {
                        Category = RetrievalBaselineCategories.NoMatch,
                        NoMatch = false
                    }
                ]
            },
            "is categorized no-match but is not labeled no-match");
    }

    [Fact]
    public void Validate_AcceptsANoMatchQueryThatExpectsNothing()
    {
        var dataset = Dataset();
        var accepted = dataset with
        {
            Queries =
            [
                dataset.Queries[0] with
                {
                    Category = RetrievalBaselineCategories.NoMatch,
                    ExpectedDocumentIds = [],
                    ExpectedChunkIds = null,
                    NoMatch = true
                }
            ]
        };

        Assert.Same(accepted, new RetrievalBaselineDatasetValidator().Validate(accepted));
    }

    [Fact]
    public void Validate_RejectsExpectedDocumentsEmbeddedWithAnIncompatibleModel()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with
            {
                Queries = [dataset.Queries[0] with { ExpectedDocumentIds = ["doc-model-incompatible"] }]
            },
            "embedded with an incompatible model");
    }

    [Fact]
    public void Validate_RejectsABlankEmbeddingModelOverride()
    {
        var dataset = Dataset();
        AssertRejects(
            dataset with { Corpus = [dataset.Corpus[0] with { EmbeddingModel = " " }, dataset.Corpus[1]] },
            "declares a blank embedding model");
    }

    [Fact]
    public void Validate_RejectsAnOrdinaryTenantEvenWhenTheDatasetPrefixPermitsIt()
    {
        AssertRejects(OrdinaryTenantDataset(), "outside the fixed benchmark tenant namespace");
    }

    private static RetrievalBaselineDataset OrdinaryTenantDataset()
    {
        const string ordinaryTenant = "tenant-prod";

        return new RetrievalBaselineDataset(
            "test-v1",
            ordinaryTenant,
            new RetrievalBaselineGates(0.9, 1, 3),
            [Document("doc-a", ordinaryTenant, "bench-alice", "TenantPublic")],
            [
                new RetrievalBaselineQuery(
                    "q-1",
                    "Which document mentions alpha?",
                    ordinaryTenant,
                    "bench-alice",
                    RetrievalBaselineCategories.Relevant,
                    ["doc-a"])
            ]);
    }

    private static void AssertRejects(RetrievalBaselineDataset dataset, string expectedFragment)
    {
        var exception = Assert.Throws<EvaluationValidationException>(
            () => new RetrievalBaselineDatasetValidator().Validate(dataset));

        Assert.Contains(expectedFragment, exception.Message, StringComparison.Ordinal);
    }

    private static RetrievalBaselineDataset WithGates(RetrievalBaselineGates gates)
    {
        return Dataset() with { Gates = gates };
    }

    private static RetrievalBaselineDataset Dataset()
    {
        return new RetrievalBaselineDataset(
            "test-v1",
            TenantPrefix,
            new RetrievalBaselineGates(0.9, 1, 3),
            [
                Document("doc-a", TenantPrefix, "bench-alice", "TenantPublic"),
                Document("doc-b", TenantPrefix, "bench-alice", "TenantPublic"),
                Document("doc-private", TenantPrefix, "bench-bob", "Private"),
                Document("doc-incompatible", TenantPrefix, "bench-alice", "TenantPublic", "mock-other"),
                Document(
                    "doc-model-incompatible",
                    TenantPrefix,
                    "bench-alice",
                    "TenantPublic",
                    embeddingModel: "mock-embedding-legacy"),
                Document("doc-other-tenant", $"{TenantPrefix}-other", "bench-carol", "TenantPublic")
            ],
            [
                new RetrievalBaselineQuery(
                    "q-1",
                    "Which document mentions alpha?",
                    TenantPrefix,
                    "bench-alice",
                    RetrievalBaselineCategories.Relevant,
                    ["doc-a"],
                    false,
                    ["chunk-a-1"])
            ]);
    }

    private static RetrievalBaselineDocument Document(
        string id,
        string tenantId,
        string ownerUserId,
        string accessLevel,
        string? embeddingProvider = null,
        string? embeddingModel = null)
    {
        var slug = id.Replace("doc-", string.Empty, StringComparison.Ordinal);

        return new RetrievalBaselineDocument(
            id,
            tenantId,
            ownerUserId,
            accessLevel,
            1,
            $"Title {slug}",
            $"{slug}.md",
            [new RetrievalBaselineChunk($"chunk-{slug}-1", $"Synthetic text for {slug}.")],
            embeddingProvider,
            embeddingModel);
    }
}
