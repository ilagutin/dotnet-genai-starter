extern alias McpHost;
using GenAIPlatform.Application.Core;
using GenAIPlatform.Application.Core.Dispatching;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Usage;
using GenAIPlatform.Application.Usage.GetUsage;
using McpHost::GenAIPlatform.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

namespace GenAIPlatform.IntegrationTests;

public sealed class McpUsageToolTests
{
    [Fact]
    public async Task GetUsageAsync_FormatsUsageSummary()
    {
        var tool = new UsageTool(new StubDispatcher(new UsageSummary(
            Requests: 3,
            InputTokens: 120,
            OutputTokens: 45,
            EmbeddingTokens: 30,
            EstimatedCost: 0.0123m,
            Currency: "USD")));

        var markdown = await tool.GetUsageAsync(model: "mock-chat");

        Assert.Contains("# Usage Summary", markdown, StringComparison.Ordinal);
        Assert.Contains("requests: 3", markdown, StringComparison.Ordinal);
        Assert.Contains("inputTokens: 120", markdown, StringComparison.Ordinal);
        Assert.Contains("estimatedCost: 0.0123", markdown, StringComparison.Ordinal);
        Assert.Contains("currency: USD", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUsageAsync_MapsApplicationValidationToMcpError()
    {
        var tool = new UsageTool(new StubDispatcher(
            new UsageQueryValidationException("from must be before or equal to to.")));

        var exception = await Assert.ThrowsAsync<McpException>(() => tool.GetUsageAsync(
            DateTimeOffset.Parse("2026-06-22T00:00:00Z"),
            DateTimeOffset.Parse("2026-06-21T00:00:00Z")));

        Assert.Contains("get_usage failed", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "alice", "tenant-a", "admin", "bob", "tenant-b", false, "An authenticated user is required.")]
    [InlineData(false, "alice", "tenant-a", "admin", null, null, true, "An authenticated user is required.")]
    [InlineData(true, "alice", "tenant-a", "developer", null, "tenant-b", false, "Usage tenant filter must match the authenticated tenant.")]
    [InlineData(true, "alice", "tenant-a", "developer", "bob", null, false, "Usage user filter must match the authenticated user.")]
    [InlineData(true, "alice", "tenant-a", "developer", "bob", "tenant-b", false, "Usage tenant filter must match the authenticated tenant.")]
    [InlineData(true, "system", null, "developer", null, null, false, "An authenticated user and tenant are required.")]
    [InlineData(true, "alice", "tenant-a", "developer", null, null, true, "from must be before or equal to to.")]
    public async Task RealDispatcher_RejectsDeniedOrInvalidScopeWithoutStorage(
        bool authenticated, string? user, string? tenant, string role, string? requestedUser,
        string? requestedTenant, bool reversedDates, string expectedDetail)
    {
        var repository = new CapturingUsageRepository();
        using var provider = CreateProvider(new UsageUserContext(authenticated, user, tenant, role), repository);
        using var scope = provider.CreateScope();
        var tool = new UsageTool(scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>());

        var exception = await Assert.ThrowsAsync<McpException>(() => tool.GetUsageAsync(
            reversedDates ? DateTimeOffset.MaxValue : null,
            reversedDates ? DateTimeOffset.MinValue : null,
            requestedUser, requestedTenant));

        Assert.Equal("get_usage failed: " + expectedDetail, exception.Message);
        Assert.Empty(repository.Queries);
    }

    [Theory]
    [InlineData("developer", null, null, "alice", "tenant-a")]
    [InlineData("developer", " ", "", "alice", "tenant-a")]
    [InlineData("developer", "alice", "tenant-a", "alice", "tenant-a")]
    [InlineData("AdMiN", "bob", "tenant-b", "bob", "tenant-b")]
    [InlineData("admin", null, null, null, null)]
    public async Task RealDispatcher_PermitsOwnAndAdminScopesWithExactFilters(
        string role, string? user, string? tenant, string? expectedUser, string? expectedTenant)
    {
        var repository = new CapturingUsageRepository();
        using var provider = CreateProvider(new UsageUserContext(true, "alice", "tenant-a", role), repository);
        using var scope = provider.CreateScope();
        var tool = new UsageTool(scope.ServiceProvider.GetRequiredService<IApplicationDispatcher>());
        var date = DateTimeOffset.Parse("2026-05-01T00:00:00Z");

        var result = await tool.GetUsageAsync(date, date, user, tenant, "mock-chat");

        Assert.Contains("# Usage Summary", result, StringComparison.Ordinal);
        Assert.Equal(new UsageQuery(date, date, expectedUser, expectedTenant, "mock-chat"), Assert.Single(repository.Queries));
    }

    private static ServiceProvider CreateProvider(IUserContext context, IUsageRepository repository)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddLogging();
        services.AddApplicationCore(configuration);
        services.AddUsageApplication();
        services.AddSingleton(context);
        services.AddSingleton(repository);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class UsageUserContext(bool authenticated, string? user, string? tenant, string role) : IUserContext
    {
        public bool IsAuthenticated => authenticated;
        public string? UserId => user;
        public string? TenantId => tenant;
        public IReadOnlyCollection<string> Roles => [role];
        public IReadOnlyCollection<string> Groups => [];
    }

    private sealed class CapturingUsageRepository : IUsageRepository
    {
        public List<UsageQuery> Queries { get; } = [];

        public Task<UsageSummary> GetUsageAsync(UsageQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(new UsageSummary(0, 0, 0, 0, 0, "USD"));
        }
    }

    private sealed class StubDispatcher(object result) : IApplicationDispatcher
    {
        public Task<TResponse> DispatchAsync<TRequest, TResponse>(
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : IRequest<TResponse>
        {
            if (result is Exception exception)
            {
                return Task.FromException<TResponse>(exception);
            }

            Assert.IsType<UsageQuery>(request);
            return Task.FromResult((TResponse)result);
        }
    }
}
