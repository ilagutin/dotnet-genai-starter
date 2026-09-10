namespace GenAIPlatform.Domain.Evaluations.Retrieval;

/// <summary>
/// The fixed namespace every retrieval baseline tenant must live in. A dataset declares
/// its own tenant prefix, but that prefix is dataset-supplied input: this literal is the
/// invariant a baseline run is allowed to address, so a permissive or mistyped dataset
/// prefix can never widen a run onto ordinary tenants.
/// </summary>
public static class RetrievalBaselineTenants
{
    public const string RequiredPrefix = "retrieval-baseline";

    public static bool IsBenchmarkTenant(string? tenantId)
    {
        return !string.IsNullOrWhiteSpace(tenantId) &&
               tenantId.StartsWith(RequiredPrefix, StringComparison.Ordinal);
    }
}
