namespace GenAIPlatform.Infrastructure.Migrations;

/// <summary>
/// A sanitized schema migration failure. Messages name the migration version and a stable
/// error code, never the migration SQL, the raw provider message or connection details.
/// </summary>
public sealed class SchemaMigrationException : Exception
{
    public SchemaMigrationException(string message, string errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public SchemaMigrationException(string message, string errorCode, Exception? innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
