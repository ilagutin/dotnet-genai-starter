namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// Pure retrieval scoring at document granularity. Retrieved document ids are compared
/// after removing duplicates while keeping retrieval order, so one document contributes
/// at most one rank position no matter how many of its chunks were retrieved.
/// </summary>
public static class RetrievalMetricsCalculator
{
    /// <summary>
    /// Scores one query against the distinct documents its retrieval returned.
    /// Recall@K is the share of labeled relevant documents found in
    /// <paramref name="retrievedDocumentIds" />; queries labeling no relevant document
    /// report null recall and null rank and count only toward no-match accuracy.
    /// </summary>
    public static RetrievalBaselineQueryResult Score(
        RetrievalBaselineQuery query,
        IReadOnlyList<string> retrievedDocumentIds)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(retrievedDocumentIds);

        var retrieved = Distinct(retrievedDocumentIds);
        var relevant = Distinct(query.ExpectedDocumentIds ?? []);
        var relevantSet = relevant.ToHashSet(StringComparer.Ordinal);
        var foundCount = retrieved.Count(relevantSet.Contains);
        var firstRelevantRank = FindFirstRelevantRank(retrieved, relevantSet);
        var hasRelevant = relevant.Count > 0;

        return new RetrievalBaselineQueryResult(
            query.Id,
            query.Category,
            query.NoMatch,
            relevant,
            retrieved,
            hasRelevant ? (double)foundCount / relevant.Count : null,
            hasRelevant ? firstRelevantRank : null,
            firstRelevantRank is null || !hasRelevant ? 0 : 1d / firstRelevantRank.Value,
            hasRelevant ? firstRelevantRank is not null : retrieved.Count == 0);
    }

    /// <summary>
    /// Aggregates scored queries. No-match accuracy is the share of no-match queries whose
    /// retrieval returned nothing and is reported as 1 when the dataset labels no no-match
    /// query. Mean recall and mean reciprocal rank are 0 when no query is recall eligible.
    /// </summary>
    public static RetrievalMetrics Aggregate(IReadOnlyList<RetrievalBaselineQueryResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var eligible = results.Where(static result => result.RecallAtK is not null).ToArray();
        var noMatch = results.Where(static result => result.NoMatch).ToArray();
        var emptyNoMatch = noMatch.Count(static result => result.RetrievedDocumentIds.Count == 0);

        return new RetrievalMetrics(
            results.Count,
            eligible.Length,
            eligible.Length == 0 ? 0 : eligible.Sum(static result => result.RecallAtK ?? 0) / eligible.Length,
            eligible.Length == 0 ? 0 : eligible.Sum(static result => result.ReciprocalRank) / eligible.Length,
            noMatch.Length,
            noMatch.Length == 0 ? 1 : (double)emptyNoMatch / noMatch.Length,
            CountBy(results, static _ => true),
            CountBy(results, static result => result.Hit));
    }

    private static int? FindFirstRelevantRank(
        IReadOnlyList<string> retrieved,
        HashSet<string> relevant)
    {
        for (var index = 0; index < retrieved.Count; index++)
        {
            if (relevant.Contains(retrieved[index]))
            {
                return index + 1;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, int> CountBy(
        IReadOnlyList<RetrievalBaselineQueryResult> results,
        Func<RetrievalBaselineQueryResult, bool> predicate)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            counts.TryAdd(result.Category, 0);
            if (predicate(result))
            {
                counts[result.Category]++;
            }
        }

        return counts;
    }

    private static IReadOnlyList<string> Distinct(IReadOnlyList<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<string>(values.Count);
        foreach (var value in values)
        {
            if (seen.Add(value))
            {
                distinct.Add(value);
            }
        }

        return distinct;
    }
}
