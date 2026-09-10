using System.Net;
using System.Net.Http.Json;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Usage.GetUsage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GenAIPlatform.IntegrationTests;

public sealed partial class ApiV1EndpointTests
{
    [Fact]
    public async Task UsageEndpoint_AppliesFiltersAndReturnsSummary()
    {
        using var usageFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUsageRepository>();
                services.AddSingleton<CapturingUsageRepository>();
                services.AddSingleton<IUsageRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<CapturingUsageRepository>());
            }));
        using var client = usageFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/usage?from=2026-05-01T00:00:00Z&to=2026-05-15T00:00:00Z&userId=alice&tenantId=tenant-a&model=mock-chat");
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "tenant-a");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<UsageSummaryResponse>();
        Assert.NotNull(body);
        Assert.Equal(2, body.Requests);
        Assert.Equal(120, body.InputTokens);
        Assert.Equal(30, body.OutputTokens);
        Assert.Equal(0.0042m, body.EstimatedCost);

        var repository = usageFactory.Services.GetRequiredService<CapturingUsageRepository>();
        Assert.NotNull(repository.Query);
        Assert.Equal("alice", repository.Query.UserId);
        Assert.Equal("tenant-a", repository.Query.TenantId);
        Assert.Equal("mock-chat", repository.Query.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-05-01T00:00:00Z"), repository.Query.FromUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-05-15T00:00:00Z"), repository.Query.ToUtc);
    }

    [Fact]
    public async Task UsageEndpoint_RejectsNonAdminCrossTenantFilters()
    {
        using var usageFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUsageRepository>();
                services.AddSingleton<CapturingUsageRepository>();
                services.AddSingleton<IUsageRepository>(
                    serviceProvider => serviceProvider.GetRequiredService<CapturingUsageRepository>());
            }));
        using var client = usageFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/usage?tenantId=tenant-b&userId=alice");
        request.Headers.Add("X-Demo-User-Id", "alice");
        request.Headers.Add("X-Demo-Tenant-Id", "tenant-a");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal("Forbidden", problem.Title);
        Assert.Equal(403, problem.Status);
        var repository = usageFactory.Services.GetRequiredService<CapturingUsageRepository>();
        Assert.Null(repository.Query);
    }

    [Theory]
    [InlineData(false, "alice", "tenant-a", "admin", "?tenantId=tenant-b", 401, "Unauthorized")]
    [InlineData(false, "alice", "tenant-a", "admin", "?from=2026-06-02&to=2026-06-01", 401, "Unauthorized")]
    [InlineData(true, "alice", "tenant-a", "developer", "?tenantId=tenant-b", 403, "Forbidden")]
    [InlineData(true, "alice", "tenant-a", "developer", "?userId=bob", 403, "Forbidden")]
    [InlineData(true, "alice", "tenant-a", "developer", "?userId=bob&tenantId=tenant-b", 403, "Forbidden")]
    [InlineData(true, null, "tenant-a", "developer", "", 401, "Unauthorized")]
    [InlineData(true, "system", null, "developer", "", 401, "Unauthorized")]
    [InlineData(true, "alice", " ", "developer", "", 401, "Unauthorized")]
    [InlineData(true, "alice", "tenant-a", "developer", "?from=2026-06-02&to=2026-06-01", 400, "Request validation failed")]
    [InlineData(true, null, null, "admin", "?from=2026-06-02&to=2026-06-01", 400, "Request validation failed")]
    public async Task UsageEndpoint_DenialsReturnProblemDetailsWithoutStorage(
        bool authenticated, string? user, string? tenant, string role, string query, int status, string title)
    {
        var repository = new CapturingUsageRepository();
        using var usageFactory = CreateUsageFactory(new UsageUserContext(authenticated, user, tenant, role), repository);
        using var client = usageFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var response = await client.GetAsync("/api/v1/usage" + query);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(status, problem.Status);
        Assert.Equal(title, problem.Title);
        Assert.False(problem.Extensions.ContainsKey("errorCode"));
        Assert.Equal(0, repository.Calls);
    }

    [Theory]
    [InlineData("developer", "", "alice", "tenant-a")]
    [InlineData("developer", "&userId=%20&tenantId=%20", "alice", "tenant-a")]
    [InlineData("developer", "&userId=alice&tenantId=tenant-a", "alice", "tenant-a")]
    [InlineData("AdMiN", "&userId=bob&tenantId=tenant-b", "bob", "tenant-b")]
    [InlineData("admin", "", null, null)]
    public async Task UsageEndpoint_AllowedScopePreservesFilters(string role, string query, string? expectedUser, string? expectedTenant)
    {
        var repository = new CapturingUsageRepository();
        using var usageFactory = CreateUsageFactory(new UsageUserContext(true, "alice", "tenant-a", role), repository);
        using var client = usageFactory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        var response = await client.GetAsync("/api/v1/usage?from=2026-05-01&to=2026-05-01&model=mock-chat" + query);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, repository.Calls);
        Assert.Equal(new UsageQuery(DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-05-01T00:00:00Z"), expectedUser, expectedTenant, "mock-chat"), repository.Query);
    }

    private WebApplicationFactory<Program> CreateUsageFactory(IUserContext context, IUsageRepository repository) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUserContext>();
                services.AddSingleton(context);
                services.RemoveAll<IUsageRepository>();
                services.AddSingleton(repository);
            });
        });

    private sealed class UsageUserContext(bool authenticated, string? user, string? tenant, string role) : IUserContext
    {
        public bool IsAuthenticated => authenticated;
        public string? UserId => user;
        public string? TenantId => tenant;
        public IReadOnlyCollection<string> Roles => [role];
        public IReadOnlyCollection<string> Groups => [];
    }

}
