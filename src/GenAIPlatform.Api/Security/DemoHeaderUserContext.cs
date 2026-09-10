using GenAIPlatform.Api.Configuration;
using GenAIPlatform.Application.Core.Security;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Api.Security;

internal sealed class DemoHeaderUserContext(
    IHttpContextAccessor httpContextAccessor,
    IHostEnvironment environment,
    IOptions<DemoAuthOptions> options)
    : IUserContext
{
    private const string UserIdHeader = "X-Demo-User-Id";
    private const string TenantIdHeader = "X-Demo-Tenant-Id";
    private const string RolesHeader = "X-Demo-Roles";
    private const string GroupsHeader = "X-Demo-Groups";

    public bool IsAuthenticated => options.Value.Enabled && ResolveUserId() is not null;

    public string? UserId => options.Value.Enabled ? ResolveUserId() : null;

    public string? TenantId => IsAuthenticated ? ResolveTenantId() : null;

    public IReadOnlyCollection<string> Roles =>
        IsAuthenticated ? GetIdentityValues(RolesHeader, options.Value.DefaultRoles) : [];

    public IReadOnlyCollection<string> Groups =>
        IsAuthenticated ? GetIdentityValues(GroupsHeader, options.Value.DefaultGroups) : [];

    private string? ResolveUserId()
    {
        return GetHeaderValue(UserIdHeader) ?? GetDevelopmentDefault(options.Value.DefaultUserId);
    }

    private string? ResolveTenantId()
    {
        return GetHeaderValue(TenantIdHeader) ?? GetDevelopmentDefault(options.Value.DefaultTenantId);
    }

    private string? GetHeaderValue(string name)
    {
        var headers = httpContextAccessor.HttpContext?.Request.Headers;
        if (headers is null || !headers.TryGetValue(name, out var values))
        {
            return null;
        }

        var value = values.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private string? GetDevelopmentDefault(string? value)
    {
        return environment.IsDevelopment() ? Normalize(value) : null;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private IReadOnlyCollection<string> GetIdentityValues(
        string name,
        IReadOnlyCollection<string> fallback)
    {
        var value = GetHeaderValue(name);
        if (value is not null)
        {
            return NormalizeIdentityValues(
                value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (!environment.IsDevelopment())
        {
            return [];
        }

        return NormalizeIdentityValues(fallback);
    }

    private static IReadOnlyCollection<string> NormalizeIdentityValues(IEnumerable<string> values)
    {
        return values
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
