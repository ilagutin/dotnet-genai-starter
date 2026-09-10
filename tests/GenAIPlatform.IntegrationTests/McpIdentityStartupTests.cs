extern alias McpHost;
using GenAIPlatform.Application.Core.Security;
using McpHost::GenAIPlatform.Mcp;
using McpHost::GenAIPlatform.Mcp.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.IntegrationTests;

public sealed class McpIdentityStartupTests
{
    private const string IdentityStartupDiagnosticsTypeName = "McpIdentityStartupDiagnostics";

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("Admin")]
    [InlineData("admin")]
    public async Task StartAsync_WarnsOnceWhenAdminRoleConfigured(string adminRoleCasing)
    {
        var logs = new CapturingLogger();
        using var provider = BuildProvider(logs, new Dictionary<string, string?>
        {
            ["GenAIPlatform:Mcp:Identity:UserId"] = "svc-user",
            ["GenAIPlatform:Mcp:Identity:TenantId"] = "svc-tenant",
            ["GenAIPlatform:Mcp:Identity:Roles:0"] = "developer",
            ["GenAIPlatform:Mcp:Identity:Roles:1"] = adminRoleCasing
        });

        await StartHostedServicesAsync(provider);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("admin", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("svc-user", entry.Message, StringComparison.Ordinal);
        Assert.Contains("svc-tenant", entry.Message, StringComparison.Ordinal);
        Assert.Contains("cross-tenant", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local", entry.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("per-caller authentication", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartAsync_LogsNothingForNonAdminRoles()
    {
        var logs = new CapturingLogger();
        using var provider = BuildProvider(logs, new Dictionary<string, string?>
        {
            ["GenAIPlatform:Mcp:Identity:UserId"] = "svc-user",
            ["GenAIPlatform:Mcp:Identity:TenantId"] = "svc-tenant"
            // Roles intentionally omitted so the ["developer"] default from McpIdentityOptions is
            // what actually gets exercised below, instead of an explicitly configured role.
        });

        using var scope = provider.CreateScope();
        var hostedServices = scope.ServiceProvider.GetServices<IHostedService>().ToArray();

        // If the diagnostics hosted service is ever removed or unregistered, this fails instead of
        // vacuously passing: with nothing to start, there would be nothing to log either.
        var diagnostics = Assert.Single(
            hostedServices,
            service => service.GetType().Name == IdentityStartupDiagnosticsTypeName);

        await diagnostics.StartAsync(CancellationToken.None);

        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Warning);

        var userContext = scope.ServiceProvider.GetRequiredService<IUserContext>();
        Assert.Equal(["developer"], userContext.Roles);
    }

    [Theory]
    [InlineData("", "svc-tenant", "GenAIPlatform:Mcp:Identity:UserId")]
    [InlineData("svc-user", "", "GenAIPlatform:Mcp:Identity:TenantId")]
    public async Task StartAsync_BlankIdentityFailsStartupWithoutLogging(
        string userId, string tenantId, string missingConfigurationKey)
    {
        var logs = new CapturingLogger();
        var marker = new StartMarker();
        using var host = BuildHost(
            logs,
            new Dictionary<string, string?>
            {
                ["GenAIPlatform:Mcp:Identity:UserId"] = userId,
                ["GenAIPlatform:Mcp:Identity:TenantId"] = tenantId,
                ["GenAIPlatform:Mcp:Identity:Roles:0"] = "admin"
            },
            marker);

        // A real IHost, not a bare ServiceProvider: this exercises the generic host's normal
        // startup validation path (wired by AddMcpUserContext's ValidateOnStart()) rather than
        // reaching OptionsValidationException only through the diagnostics service's own
        // options.Value access.
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync());

        Assert.Contains(missingConfigurationKey, exception.Message, StringComparison.Ordinal);

        // The generic host itself logs a generic "Hosting failed to start" error when startup
        // validation fails; what must never happen is the admin-identity *warning*, since the
        // identity was never accepted as valid in the first place.
        Assert.DoesNotContain(logs.Entries, entry => entry.Level == LogLevel.Warning);

        // ValidateOnStart() runs before ANY hosted service starts, so a marker hosted service
        // registered ahead of the identity options must never observe a start either. Without
        // ValidateOnStart(), the host would start hosted services in registration order and this
        // marker would run before the diagnostics service later throws from its own options.Value
        // access, making this assertion the one that actually depends on ValidateOnStart() being
        // present (unlike asserting on the exception alone, which the diagnostics service's own
        // unconditional options.Value access would also produce on its own).
        Assert.False(
            marker.Started,
            "No hosted service should start once startup option validation has failed.");
    }

    [Fact]
    public async Task StartAsync_TruncatesIdentifierLongerThan64CharactersInTheMessage()
    {
        var longUserId = new string('u', 100);
        var logs = new CapturingLogger();
        using var provider = BuildProvider(logs, new Dictionary<string, string?>
        {
            ["GenAIPlatform:Mcp:Identity:UserId"] = longUserId,
            ["GenAIPlatform:Mcp:Identity:TenantId"] = "svc-tenant",
            ["GenAIPlatform:Mcp:Identity:Roles:0"] = "admin"
        });

        await StartHostedServicesAsync(provider);

        var entry = Assert.Single(logs.Entries);
        Assert.Contains(new string('u', 64), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('u', 65), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_RemovesControlCharactersFromIdentifierSoItCannotForgeALogLine()
    {
        var logs = new CapturingLogger();
        using var provider = BuildProvider(logs, new Dictionary<string, string?>
        {
            ["GenAIPlatform:Mcp:Identity:UserId"] = "svc-user\n\r\u0001FAKE LOG LINE",
            ["GenAIPlatform:Mcp:Identity:TenantId"] = "svc-tenant",
            ["GenAIPlatform:Mcp:Identity:Roles:0"] = "admin"
        });

        await StartHostedServicesAsync(provider);

        // Exactly one entry proves the crafted identifier could not split the warning into a
        // second, attacker-controlled log line.
        var entry = Assert.Single(logs.Entries);
        Assert.DoesNotContain('\n', entry.Message);
        Assert.DoesNotContain('\r', entry.Message);
        Assert.DoesNotContain('\u0001', entry.Message);
        Assert.Contains("svc-userFAKE LOG LINE", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_NeverIncludesRolesOrGroupsValuesInTheMessage()
    {
        var logs = new CapturingLogger();
        using var provider = BuildProvider(logs, new Dictionary<string, string?>
        {
            ["GenAIPlatform:Mcp:Identity:UserId"] = "svc-user",
            ["GenAIPlatform:Mcp:Identity:TenantId"] = "svc-tenant",
            ["GenAIPlatform:Mcp:Identity:Roles:0"] = "admin",
            ["GenAIPlatform:Mcp:Identity:Roles:1"] = "distinctive-role-abc123",
            ["GenAIPlatform:Mcp:Identity:Groups:0"] = "distinctive-group-xyz123"
        });

        await StartHostedServicesAsync(provider);

        var entry = Assert.Single(logs.Entries);
        Assert.DoesNotContain("distinctive-role-abc123", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("distinctive-group-xyz123", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_RemovesLineSeparatorCharacterFromIdentifier()
    {
        var logs = new CapturingLogger();
        using var provider = BuildProvider(logs, new Dictionary<string, string?>
        {
            ["GenAIPlatform:Mcp:Identity:UserId"] = "svc-user\u2028next-line",
            ["GenAIPlatform:Mcp:Identity:TenantId"] = "svc-tenant",
            ["GenAIPlatform:Mcp:Identity:Roles:0"] = "admin"
        });

        await StartHostedServicesAsync(provider);

        var entry = Assert.Single(logs.Entries);
        Assert.DoesNotContain('\u2028', entry.Message);
        Assert.Contains("svc-usernext-line", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartAsync_TruncatesWithoutSplittingASurrogatePair()
    {
        // U+1F600 is an astral character encoded as a UTF-16 surrogate pair. Placed right after 63
        // ASCII characters, the pair straddles the 64-code-unit truncation cut.
        const string astralCharacter = "\U0001F600";
        var userId = new string('u', 63) + astralCharacter + "trailing";

        var logs = new CapturingLogger();
        using var provider = BuildProvider(logs, new Dictionary<string, string?>
        {
            ["GenAIPlatform:Mcp:Identity:UserId"] = userId,
            ["GenAIPlatform:Mcp:Identity:TenantId"] = "svc-tenant",
            ["GenAIPlatform:Mcp:Identity:Roles:0"] = "admin"
        });

        await StartHostedServicesAsync(provider);

        var entry = Assert.Single(logs.Entries);

        for (var index = 0; index < entry.Message.Length; index++)
        {
            if (char.IsHighSurrogate(entry.Message[index]))
            {
                Assert.True(
                    index + 1 < entry.Message.Length && char.IsLowSurrogate(entry.Message[index + 1]),
                    "A high surrogate must always be followed by its low surrogate.");
            }
            else
            {
                Assert.False(char.IsLowSurrogate(entry.Message[index]));
            }
        }

        Assert.Contains(new string('u', 63), entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(astralCharacter, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("trailing", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddGenAIPlatformMcp_RegistersIdentityStartupDiagnosticsHostedService()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.mcp.test.json", optional: false)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddGenAIPlatformMcp(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using var scope = provider.CreateScope();
        var hostedServices = scope.ServiceProvider.GetServices<IHostedService>();

        Assert.Contains(
            hostedServices,
            service => service.GetType().Name == IdentityStartupDiagnosticsTypeName);
    }

    private static ServiceProvider BuildProvider(CapturingLogger logs, Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(logs));
        services.AddMcpUserContext(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static IHost BuildHost(
        CapturingLogger logs, IReadOnlyDictionary<string, string?> settings, StartMarker marker)
    {
        return new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging(builder => builder.AddProvider(logs));

                // Registered before AddMcpUserContext so its hosted service would be the first to
                // start (in DI registration order) if hosted services ever got a chance to run.
                services.AddSingleton(marker);
                services.AddHostedService<MarkerHostedService>();

                services.AddMcpUserContext(context.Configuration);
            })
            .Build();
    }

    private static async Task StartHostedServicesAsync(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        foreach (var hostedService in scope.ServiceProvider.GetServices<IHostedService>())
        {
            await hostedService.StartAsync(CancellationToken.None);
        }
    }

    private sealed class CapturingLogger : ILogger, ILoggerProvider
    {
        public List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Dispose()
        {
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class StartMarker
    {
        public bool Started { get; set; }
    }

    private sealed class MarkerHostedService(StartMarker marker) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            marker.Started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
