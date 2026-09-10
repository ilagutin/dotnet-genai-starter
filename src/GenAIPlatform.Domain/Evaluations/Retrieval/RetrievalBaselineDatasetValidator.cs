using GenAIPlatform.Domain.Documents;
using GenAIPlatform.Domain.Exceptions;

namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// Fails a retrieval baseline dataset closed before any database row is replaced or any
/// query is scored. Every tenant must satisfy both the dataset's own prefix and the fixed
/// <see cref="RetrievalBaselineTenants.RequiredPrefix" />, so a baseline run can never
/// address tenants outside the isolated benchmark namespace; the readability rule keeps a
/// dataset from labeling a document the query's caller is not allowed to retrieve, and the
/// no-match rule keeps a query's label and its expectations from disagreeing.
/// </summary>
public sealed class RetrievalBaselineDatasetValidator
{
    public RetrievalBaselineDataset Validate(RetrievalBaselineDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        if (string.IsNullOrWhiteSpace(dataset.Version))
        {
            throw Invalid("Retrieval baseline dataset version is required.");
        }

        if (string.IsNullOrWhiteSpace(dataset.TenantPrefix))
        {
            throw Invalid("Retrieval baseline dataset tenant prefix is required.");
        }

        ValidateGates(dataset);
        var documents = ValidateCorpus(dataset);
        ValidateQueries(dataset, documents);

        return dataset;
    }

    private static void ValidateGates(RetrievalBaselineDataset dataset)
    {
        if (dataset.Gates is null)
        {
            throw Invalid($"Retrieval baseline dataset '{dataset.Version}' must define gates.");
        }

        if (dataset.Gates.K < RetrievalBaselineGates.MinDepth ||
            dataset.Gates.K > RetrievalBaselineGates.MaxDepth)
        {
            throw Invalid(
                $"Retrieval baseline dataset '{dataset.Version}' gate k must be between {RetrievalBaselineGates.MinDepth} and {RetrievalBaselineGates.MaxDepth}.");
        }

        EnsureShare(dataset.Version, "minRecallAtK", dataset.Gates.MinRecallAtK);
        EnsureShare(dataset.Version, "minNoMatchAccuracy", dataset.Gates.MinNoMatchAccuracy);
    }

    private static IReadOnlyDictionary<string, RetrievalBaselineDocument> ValidateCorpus(
        RetrievalBaselineDataset dataset)
    {
        if (dataset.Corpus is not { Count: > 0 })
        {
            throw Invalid(
                $"Retrieval baseline dataset '{dataset.Version}' must contain at least one corpus document.");
        }

        var documents = new Dictionary<string, RetrievalBaselineDocument>(StringComparer.Ordinal);
        var chunkIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in dataset.Corpus)
        {
            ValidateDocument(dataset, document);
            if (!documents.TryAdd(document.Id, document))
            {
                throw Invalid(
                    $"Retrieval baseline dataset '{dataset.Version}' declares duplicate document id '{document.Id}'.");
            }

            ValidateChunks(dataset, document, chunkIds);
        }

        return documents;
    }

    private static void ValidateDocument(
        RetrievalBaselineDataset dataset,
        RetrievalBaselineDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.Id))
        {
            throw Invalid($"Retrieval baseline dataset '{dataset.Version}' declares a document without an id.");
        }

        EnsureTenantPrefix(dataset, document.TenantId, $"document '{document.Id}'");

        if (string.IsNullOrWhiteSpace(document.OwnerUserId))
        {
            throw Invalid($"Retrieval baseline document '{document.Id}' must define an owner.");
        }

        if (!Enum.TryParse<DocumentAccessLevel>(document.AccessLevel, ignoreCase: false, out _))
        {
            throw Invalid($"Retrieval baseline document '{document.Id}' declares an unknown access level.");
        }

        if (document.Version < 1)
        {
            throw Invalid($"Retrieval baseline document '{document.Id}' must declare a version of at least 1.");
        }

        if (string.IsNullOrWhiteSpace(document.Title) || string.IsNullOrWhiteSpace(document.FileName))
        {
            throw Invalid($"Retrieval baseline document '{document.Id}' must define a title and a file name.");
        }

        if (document.EmbeddingProvider is not null && string.IsNullOrWhiteSpace(document.EmbeddingProvider))
        {
            throw Invalid($"Retrieval baseline document '{document.Id}' declares a blank embedding provider.");
        }

        if (document.EmbeddingModel is not null && string.IsNullOrWhiteSpace(document.EmbeddingModel))
        {
            throw Invalid($"Retrieval baseline document '{document.Id}' declares a blank embedding model.");
        }
    }

    private static void ValidateChunks(
        RetrievalBaselineDataset dataset,
        RetrievalBaselineDocument document,
        HashSet<string> chunkIds)
    {
        if (document.Chunks is not { Count: > 0 })
        {
            throw Invalid($"Retrieval baseline document '{document.Id}' must contain at least one chunk.");
        }

        var hasCurrentVersionChunk = false;
        foreach (var chunk in document.Chunks)
        {
            if (string.IsNullOrWhiteSpace(chunk.Id))
            {
                throw Invalid($"Retrieval baseline document '{document.Id}' declares a chunk without an id.");
            }

            if (!chunkIds.Add(chunk.Id))
            {
                throw Invalid(
                    $"Retrieval baseline dataset '{dataset.Version}' declares duplicate chunk id '{chunk.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(chunk.Text))
            {
                throw Invalid($"Retrieval baseline chunk '{chunk.Id}' must define text.");
            }

            var chunkVersion = chunk.DocumentVersion ?? document.Version;
            if (chunkVersion < 1 || chunkVersion > document.Version)
            {
                throw Invalid(
                    $"Retrieval baseline chunk '{chunk.Id}' declares a document version outside 1 to {document.Version}.");
            }

            hasCurrentVersionChunk |= chunkVersion == document.Version;
        }

        if (!hasCurrentVersionChunk)
        {
            throw Invalid(
                $"Retrieval baseline document '{document.Id}' must contain at least one chunk at its current version.");
        }
    }

    private static void ValidateQueries(
        RetrievalBaselineDataset dataset,
        IReadOnlyDictionary<string, RetrievalBaselineDocument> documents)
    {
        if (dataset.Queries is not { Count: > 0 })
        {
            throw Invalid($"Retrieval baseline dataset '{dataset.Version}' must contain at least one query.");
        }

        var queryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in dataset.Queries)
        {
            if (string.IsNullOrWhiteSpace(query.Id))
            {
                throw Invalid($"Retrieval baseline dataset '{dataset.Version}' declares a query without an id.");
            }

            if (!queryIds.Add(query.Id))
            {
                throw Invalid(
                    $"Retrieval baseline dataset '{dataset.Version}' declares duplicate query id '{query.Id}'.");
            }

            if (string.IsNullOrWhiteSpace(query.Question))
            {
                throw Invalid($"Retrieval baseline query '{query.Id}' must define a question.");
            }

            EnsureTenantPrefix(dataset, query.TenantId, $"query '{query.Id}'");

            if (string.IsNullOrWhiteSpace(query.UserId))
            {
                throw Invalid($"Retrieval baseline query '{query.Id}' must define a user.");
            }

            if (!RetrievalBaselineCategories.IsKnown(query.Category))
            {
                throw Invalid($"Retrieval baseline query '{query.Id}' declares an unknown category.");
            }

            ValidateExpectations(query, documents);
        }
    }

    private static void ValidateExpectations(
        RetrievalBaselineQuery query,
        IReadOnlyDictionary<string, RetrievalBaselineDocument> documents)
    {
        var expectedDocumentIds = query.ExpectedDocumentIds ?? [];
        if (query.NoMatch && expectedDocumentIds.Count > 0)
        {
            throw Invalid($"Retrieval baseline query '{query.Id}' is labeled no-match but expects documents.");
        }

        // A query that expects nothing and is not labeled no-match would score against no
        // metric denominator at all, so it would silently leave the benchmark. Labels and
        // expectations must agree in both directions.
        if (!query.NoMatch && expectedDocumentIds.Count == 0)
        {
            throw Invalid(
                $"Retrieval baseline query '{query.Id}' expects no documents but is not labeled no-match.");
        }

        if (string.Equals(query.Category, RetrievalBaselineCategories.NoMatch, StringComparison.Ordinal) &&
            !query.NoMatch)
        {
            throw Invalid($"Retrieval baseline query '{query.Id}' is categorized no-match but is not labeled no-match.");
        }

        var expectedDocuments = new List<RetrievalBaselineDocument>(expectedDocumentIds.Count);
        foreach (var documentId in expectedDocumentIds)
        {
            if (!documents.TryGetValue(documentId, out var document))
            {
                throw Invalid(
                    $"Retrieval baseline query '{query.Id}' expects document '{documentId}', which the corpus does not contain.");
            }

            if (!IsReadableBy(document, query))
            {
                throw Invalid(
                    $"Retrieval baseline query '{query.Id}' expects document '{documentId}', which its caller cannot read.");
            }

            if (document.EmbeddingProvider is not null)
            {
                throw Invalid(
                    $"Retrieval baseline query '{query.Id}' expects document '{documentId}', which is embedded with an incompatible provider.");
            }

            if (document.EmbeddingModel is not null)
            {
                throw Invalid(
                    $"Retrieval baseline query '{query.Id}' expects document '{documentId}', which is embedded with an incompatible model.");
            }

            expectedDocuments.Add(document);
        }

        ValidateExpectedChunks(query, expectedDocuments);
    }

    private static void ValidateExpectedChunks(
        RetrievalBaselineQuery query,
        IReadOnlyList<RetrievalBaselineDocument> expectedDocuments)
    {
        if (query.ExpectedChunkIds is not { Count: > 0 })
        {
            return;
        }

        var eligibleChunkIds = expectedDocuments
            .SelectMany(document => document.Chunks
                .Where(chunk => (chunk.DocumentVersion ?? document.Version) == document.Version)
                .Select(static chunk => chunk.Id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var chunkId in query.ExpectedChunkIds)
        {
            if (!eligibleChunkIds.Contains(chunkId))
            {
                throw Invalid(
                    $"Retrieval baseline query '{query.Id}' expects chunk '{chunkId}', which is not a current-version chunk of an expected document.");
            }
        }
    }

    private static bool IsReadableBy(
        RetrievalBaselineDocument document,
        RetrievalBaselineQuery query)
    {
        if (!string.Equals(document.TenantId, query.TenantId, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(document.AccessLevel, nameof(DocumentAccessLevel.TenantPublic), StringComparison.Ordinal) ||
               string.Equals(document.OwnerUserId, query.UserId, StringComparison.Ordinal);
    }

    private static void EnsureTenantPrefix(
        RetrievalBaselineDataset dataset,
        string? tenantId,
        string subject)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw Invalid($"Retrieval baseline {subject} must define a tenant.");
        }

        if (!tenantId.StartsWith(dataset.TenantPrefix, StringComparison.Ordinal))
        {
            throw Invalid(
                $"Retrieval baseline {subject} uses tenant '{tenantId}', which is outside the benchmark tenant prefix '{dataset.TenantPrefix}'.");
        }

        // The dataset-supplied prefix is input, not an authority. A tenant must also live
        // in the fixed benchmark namespace, so a permissive dataset prefix cannot let a
        // baseline run address an ordinary tenant.
        if (!RetrievalBaselineTenants.IsBenchmarkTenant(tenantId))
        {
            throw Invalid(
                $"Retrieval baseline {subject} uses tenant '{tenantId}', which is outside the fixed benchmark tenant namespace '{RetrievalBaselineTenants.RequiredPrefix}'.");
        }
    }

    private static void EnsureShare(
        string datasetVersion,
        string gateName,
        double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw Invalid(
                $"Retrieval baseline dataset '{datasetVersion}' gate {gateName} must be between 0 and 1.");
        }
    }

    private static EvaluationValidationException Invalid(string message)
    {
        return new EvaluationValidationException(message);
    }
}
