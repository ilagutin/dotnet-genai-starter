using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GenAIPlatform.Application.Evaluations.RetrievalBaseline;

/// <summary>
/// Produces a stable digest of the settings a baseline run measured under, so a later run
/// can show at a glance whether it is comparable. The digest covers configuration only and
/// never touches dataset text.
/// </summary>
internal static class RetrievalBaselineSettingsHasher
{
    public static string Hash(
        string embeddingProvider,
        string embeddingModel,
        string mockVariant,
        int embeddingDimensions,
        int topK,
        double minSimilarityScore)
    {
        var canonical = string.Join(
            '|',
            embeddingProvider,
            embeddingModel,
            mockVariant,
            embeddingDimensions.ToString(CultureInfo.InvariantCulture),
            topK.ToString(CultureInfo.InvariantCulture),
            minSimilarityScore.ToString("R", CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
