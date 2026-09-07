namespace GenAIPlatform.Infrastructure.Mcp;

public sealed class ExternalMcpOptions
{
    public const string SectionName = "GenAIPlatform:ExternalMcp";

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
