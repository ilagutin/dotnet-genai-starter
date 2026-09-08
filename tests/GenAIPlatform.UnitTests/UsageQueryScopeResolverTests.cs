using GenAIPlatform.Application.Core.Exceptions;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Usage.GetUsage;

namespace GenAIPlatform.UnitTests;

public sealed class UsageQueryScopeResolverTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Anonymous_IsRejectedBeforeRolesDatesAndStorage(bool throwingRoles)
    {
        var context = new TestUserContext(false, "alice", "tenant-a", ["admin"], throwingRoles);
        var repository = new CapturingRepository();
        var handler = new UsageQueryHandler(repository, new UsageQueryScopeResolver(context));
        var query = new UsageQuery(DateTimeOffset.MaxValue, DateTimeOffset.MinValue, "bob", "tenant-b");

        await Assert.ThrowsAsync<UnauthorizedRequestException>(() => handler.HandleAsync(query, CancellationToken.None));

        Assert.Equal(0, context.RoleReads);
        Assert.Empty(repository.Queries);
    }

    [Theory]
    [InlineData(null, "tenant-b")]
    [InlineData("bob", null)]
    [InlineData("bob", "tenant-b")]
    [InlineData("Alice", "tenant-a")]
    [InlineData("alice", "Tenant-a")]
    public async Task NonAdmin_CrossScopeIsForbiddenWithoutStorage(string? user, string? tenant)
    {
        var repository = new CapturingRepository();
        var handler = new UsageQueryHandler(repository, new UsageQueryScopeResolver(new TestUserContext()));

        await Assert.ThrowsAsync<ForbiddenRequestException>(() =>
            handler.HandleAsync(new UsageQuery(UserId: user, TenantId: tenant), CancellationToken.None));

        Assert.Empty(repository.Queries);
    }

    [Theory]
    [InlineData(null, "tenant-a")]
    [InlineData("", "tenant-a")]
    [InlineData(" ", "tenant-a")]
    [InlineData("alice", null)]
    [InlineData("alice", "")]
    [InlineData("alice", " ")]
    [InlineData("system", null)]
    public void NonAdmin_IncompleteIdentityIsUnauthorized(string? user, string? tenant)
    {
        var resolver = new UsageQueryScopeResolver(new TestUserContext(true, user, tenant));

        Assert.Throws<UnauthorizedRequestException>(() => resolver.Resolve(new UsageQuery()));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", " ")]
    [InlineData("alice", "tenant-a")]
    public async Task NonAdmin_ResolvesOwnScopeAndPreservesOtherFilters(string? user, string? tenant)
    {
        var date = DateTimeOffset.Parse("2026-05-01T00:00:00Z");
        var query = new UsageQuery(date, date, user, tenant, "test-model");
        var repository = new CapturingRepository();
        var handler = new UsageQueryHandler(repository, new UsageQueryScopeResolver(new TestUserContext()));

        await handler.HandleAsync(query, CancellationToken.None);

        Assert.Equal(query with { UserId = "alice", TenantId = "tenant-a" }, Assert.Single(repository.Queries));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("bob", "tenant-b")]
    public void AuthenticatedAdmin_PreservesAggregateOrCrossScopeWithoutIdentity(string? user, string? tenant)
    {
        var resolver = new UsageQueryScopeResolver(new TestUserContext(true, null, null, ["AdMiN"]));
        var query = new UsageQuery(UserId: user, TenantId: tenant, Model: "test-model");

        Assert.Same(query, resolver.Resolve(query));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("developer")]
    public void Authenticated_ReversedDatesRemainValidationError(string role)
    {
        var resolver = new UsageQueryScopeResolver(new TestUserContext(roles: [role]));

        Assert.Throws<UsageQueryValidationException>(() =>
            resolver.Resolve(new UsageQuery(DateTimeOffset.MaxValue, DateTimeOffset.MinValue)));
    }

    private sealed class TestUserContext(
        bool authenticated = true,
        string? user = "alice",
        string? tenant = "tenant-a",
        IReadOnlyCollection<string>? roles = null,
        bool throwingRoles = false) : IUserContext
    {
        public bool IsAuthenticated => authenticated;
        public string? UserId => user;
        public string? TenantId => tenant;
        public int RoleReads { get; private set; }
        public IReadOnlyCollection<string> Roles
        {
            get
            {
                RoleReads++;
                return throwingRoles ? throw new InvalidOperationException("Roles must not be accessed.") : roles ?? [];
            }
        }
        public IReadOnlyCollection<string> Groups => [];
    }

    private sealed class CapturingRepository : IUsageRepository
    {
        public List<UsageQuery> Queries { get; } = [];

        public Task<UsageSummary> GetUsageAsync(UsageQuery query, CancellationToken cancellationToken)
        {
            Queries.Add(query);
            return Task.FromResult(new UsageSummary(0, 0, 0, 0, 0, "USD"));
        }
    }
}
