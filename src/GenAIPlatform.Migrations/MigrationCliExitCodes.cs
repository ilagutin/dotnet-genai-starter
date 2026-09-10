namespace GenAIPlatform.Migrations;

/// <summary>
/// Process exit codes. They are part of the operator contract: scripts branch on them.
/// </summary>
public static class MigrationCliExitCodes
{
    public const int UpToDate = 0;
    public const int BehindOrFailed = 1;
    public const int UsageOrConfiguration = 2;
}
