namespace GenAIPlatform.Infrastructure.Mcp;

public sealed class ExternalMcpOptions
{
    public const string SectionName = "GenAIPlatform:ExternalMcp";
    public const int DefaultMaxToolResultBytes = 32 * 1024;
    public const int MaximumToolResultBytes = 1024 * 1024;

    /// <summary>
    /// Maximum UTF-8 byte count of provider-neutral JSON returned to the agentic execution path.
    /// Must be between the omission object size and 1 MiB. Oversized results are replaced with a
    /// valid omission object.
    /// </summary>
    public int MaxToolResultBytes { get; init; } = DefaultMaxToolResultBytes;

    /// <summary>
    /// Connect to enabled servers as a startup warmup. Set false for consumers that connect on
    /// demand per workflow stage instead of globally at startup. Startup never fails on a server
    /// being unavailable regardless of this flag.
    /// </summary>
    public bool ConnectOnStartup { get; init; } = true;

    /// <summary>
    /// Maximum number of servers connected concurrently during startup warmup and background
    /// recovery. Bounded so a single slow or hung server cannot head-of-line-block the others,
    /// without unbounded fan-out.
    /// </summary>
    public int MaxParallelConnects { get; init; } = 4;

    /// <summary>
    /// Interval for the background recovery pass that re-attempts servers that are not currently
    /// available (for example a server that was down at startup), gated by the connection policy.
    /// Already-available servers are left untouched. <see cref="TimeSpan.Zero"/> disables the
    /// background pass, leaving recovery to explicit <c>RefreshAsync</c> calls.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(60);

    public List<ExternalMcpServerOptions> Servers { get; init; } = [];
}
