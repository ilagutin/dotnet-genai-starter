namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// Stable, sanitized error codes for schema migration failures. Codes are safe to print;
/// they never carry SQL text, credentials or connection strings.
/// </summary>
public static class SchemaMigrationErrorCodes
{
    public const string CatalogInvalid = "migration_catalog_invalid";
    public const string NotConfigured = "migration_not_configured";
    public const string LockTimeout = "migration_lock_timeout";
    public const string Canceled = "migration_canceled";
    public const string ChecksumMismatch = "migration_checksum_mismatch";
    public const string UnknownVersion = "migration_unknown_version";
    public const string LegacySchemaUnrecognized = "migration_legacy_schema_unrecognized";
    public const string ApplyFailed = "migration_apply_failed";
    public const string StoreFailed = "migration_store_failed";
    public const string Unavailable = "migration_store_unavailable";
}
