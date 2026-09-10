using GenAIPlatform.Infrastructure.Mcp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class ExternalMcpLifecycleTests
{
    private const string Secret = "lifecycle-secret-marker";

    [Fact]
    public async Task EnabledCompositionUsesOneManagerAndOneEffectiveShutdown()
    {
        var client = new RecordingClient();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IExternalMcpClientFactory>(new RecordingFactory(client));
        services.AddExternalMcpInfrastructure(Configuration(enabled: true));
        var provider = services.BuildServiceProvider();
        var concrete = provider.GetRequiredService<ExternalMcpConnectionManager>();
        var abstraction = provider.GetRequiredService<IExternalMcpConnectionManager>();
        var hosted = Assert.Single(provider.GetServices<IHostedService>().OfType<ExternalMcpHostedService>());

        Assert.Same(concrete, abstraction);
        await hosted.StartAsync(CancellationToken.None);
        await concrete.BackgroundActivity;
        await hosted.StopAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
        await hosted.DisposeAsync();
        await provider.DisposeAsync();

        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task DisabledCompositionHasNoHostedLifecycle()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExternalMcpInfrastructure(Configuration(enabled: false));
        await using var provider = services.BuildServiceProvider();

        Assert.Empty(provider.GetServices<IHostedService>().OfType<ExternalMcpHostedService>());
        Assert.NotNull(provider.GetRequiredService<IExternalMcpConnectionManager>());
    }

    [Fact]
    public async Task CanceledStopBoundsWaitAndAsyncDisposalObservesCleanup()
    {
        var releaseDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingClient { DisposeOverride = () => new ValueTask(releaseDisposal.Task) };
        var manager = CreateManager(new RecordingFactory(client));
        var hosted = new ExternalMcpHostedService(manager);
        await manager.RefreshAsync(CancellationToken.None);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await hosted.StopAsync(canceled.Token);
        var rejected = await manager.CallToolAsync(Tool(), null, CancellationToken.None);

        Assert.Equal("mcp_server_unavailable", rejected.ErrorCode);
        Assert.Equal(1, client.DisposeCalls);
        releaseDisposal.SetResult();
        await hosted.DisposeAsync();
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task ConnectionRacingShutdownIsRejectedAndDisposedOnce()
    {
        var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingClient();
        var factory = new RecordingFactory(client)
        {
            CreateOverride = async _ =>
            {
                createEntered.SetResult();
                await releaseCreate.Task;
                return client;
            }
        };
        var manager = CreateManager(factory);
        var hosted = new ExternalMcpHostedService(manager);
        var refresh = manager.RefreshAsync(CancellationToken.None);
        await createEntered.Task;
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await hosted.StopAsync(canceled.Token);
        releaseCreate.SetResult();
        await hosted.DisposeAsync();
        await refresh;

        Assert.Equal(1, client.DisposeCalls);
        Assert.DoesNotContain(manager.GetSnapshots(), static snapshot => snapshot.IsAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RefreshAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectedPublicationWithThrowingDisposeIsObservedOnceAndSanitized()
    {
        var createEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CapturingLogger<ExternalMcpConnectionManager>();
        var client = new RecordingClient
        {
            DisposeOverride = () => throw new InvalidOperationException(Secret)
        };
        var factory = new RecordingFactory(client)
        {
            CreateOverride = async _ =>
            {
                createEntered.SetResult();
                await releaseCreate.Task;
                return client;
            }
        };
        var manager = CreateManager(factory, logger);
        var refresh = manager.RefreshAsync(CancellationToken.None);
        await createEntered.Task;
        var hosted = new ExternalMcpHostedService(manager);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await hosted.StopAsync(canceled.Token);
        releaseCreate.SetResult();
        await hosted.DisposeAsync();
        await refresh;

        Assert.Equal(1, client.DisposeCalls);
        var text = string.Join('|', logger.Messages);
        Assert.Contains(nameof(InvalidOperationException), text, StringComparison.Ordinal);
        Assert.Contains("safe_server", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.Empty(logger.Exceptions);
    }

    [Fact]
    public async Task ShutdownCancelsActiveCallAndPreventsRedispatchOrReconnect()
    {
        var callEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingClient
        {
            CallOverride = async (_, _, cancellationToken) =>
            {
                callEntered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return ExternalMcpToolCallResult.Unavailable("unreachable");
            }
        };
        var factory = new RecordingFactory(client);
        var manager = CreateManager(factory);
        var hosted = new ExternalMcpHostedService(manager);
        await manager.RefreshAsync(CancellationToken.None);

        var activeCall = manager.CallToolAsync(Tool(), null, CancellationToken.None);
        await callEntered.Task;
        var stop = hosted.StopAsync(CancellationToken.None);

        var result = await activeCall;
        await stop;
        var rejected = await manager.CallToolAsync(Tool(), null, CancellationToken.None);
        await hosted.DisposeAsync();

        Assert.Equal("mcp_tool_outcome_unknown", result.ErrorCode);
        Assert.Equal("mcp_server_unavailable", rejected.ErrorCode);
        Assert.Contains("may have completed", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Null(result.PayloadMetadata);
        Assert.Equal(1, client.CallCalls);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Equal(1, client.DisposeCalls);
        Assert.DoesNotContain(manager.GetSnapshots(), static snapshot => snapshot.IsAvailable);
    }

    [Fact]
    public async Task CancellationDuringConnectDoesNotDispatchOrReportUnknownOutcome()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingClient();
        var factory = new RecordingFactory(client)
        {
            CreateOverride = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return client;
            }
        };
        var manager = CreateManager(factory);
        using var caller = new CancellationTokenSource();
        var call = manager.CallToolAsync(Tool(), null, caller.Token);
        await entered.Task;

        await caller.CancelAsync();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        Assert.IsNotType<ExternalMcpCallCanceledException>(exception);
        Assert.Equal(0, client.CallCalls);
        Assert.Equal(1, factory.CreateCalls);
        await new ExternalMcpHostedService(manager).DisposeAsync();
    }

    [Fact]
    public async Task ShutdownDuringConnectIsUnavailableWithoutDispatch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new RecordingClient();
        var factory = new RecordingFactory(client)
        {
            CreateOverride = async token =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return client;
            }
        };
        var manager = CreateManager(factory);
        var call = manager.CallToolAsync(Tool(), null, CancellationToken.None);
        await entered.Task;
        await new ExternalMcpHostedService(manager).DisposeAsync();

        Assert.Equal("mcp_server_unavailable", (await call).ErrorCode);
        Assert.Equal(0, client.CallCalls);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public async Task ConnectionPreDispatchShutdownDoesNotDispatchOrInvalidate()
    {
        var client = new RecordingClient();
        var connection = new ExternalMcpServerConnection(Server(), client);
        using var shutdown = new CancellationTokenSource();
        await shutdown.CancelAsync();

        var attempt = await connection.TryCallAsync(
            Tool(), null, shutdown.Token, CancellationToken.None, shutdown.Token,
            NullLogger<ExternalMcpConnectionManager>.Instance);

        Assert.Equal("mcp_server_unavailable", attempt.Result.ErrorCode);
        Assert.False(attempt.MarkUnavailable);
        Assert.Equal(0, client.CallCalls);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task LifecycleLogsOnlySanitizedIdentityAndExceptionType()
    {
        var logger = new CapturingLogger<ExternalMcpConnectionManager>();
        var factory = new RecordingFactory(new RecordingClient())
        {
            CreateOverride = _ => throw new InvalidOperationException(Secret)
        };
        var manager = CreateManager(factory, logger);

        await manager.RefreshAsync(CancellationToken.None);
        await new ExternalMcpHostedService(manager).DisposeAsync();

        var text = string.Join('|', logger.Messages);
        Assert.Contains(nameof(InvalidOperationException), text, StringComparison.Ordinal);
        Assert.Contains("safe_server", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.Empty(logger.Exceptions);
    }

    [Fact]
    public async Task LifecycleLogIdentityIsBoundedAcrossConnectorAndConnection()
    {
        const string tailMarker = "never_log_this_tail";
        var hostileName = "Alpha\r\n\t" + new string('Z', 200) + tailMarker;
        var logger = new CapturingLogger<ExternalMcpConnectionManager>();
        var connectorManager = CreateManager(
            new RecordingFactory(new RecordingClient())
            {
                CreateOverride = _ => throw new InvalidOperationException(Secret)
            },
            logger,
            Server(hostileName));
        await connectorManager.RefreshAsync(CancellationToken.None);
        await new ExternalMcpHostedService(connectorManager).DisposeAsync();

        var callClient = new RecordingClient
        {
            CallOverride = (_, _, _) => throw new InvalidOperationException(Secret)
        };
        var connection = new ExternalMcpServerConnection(Server(hostileName), callClient);
        await connection.TryCallAsync(
            Tool(ExternalMcpNameSanitizer.SanitizeSegment(hostileName, "server")),
            null,
            CancellationToken.None,
            CancellationToken.None,
            CancellationToken.None,
            logger);
        await connection.DisposeSafelyAsync(logger);

        var identities = logger.Fields.Select(fields => Assert.IsType<string>(fields["ServerName"])).ToArray();
        Assert.Equal(2, identities.Length);
        Assert.All(identities, identity =>
        {
            Assert.True(identity.Length <= ExternalMcpNameSanitizer.MaxLogIdentityLength);
            Assert.Matches("^[a-z0-9_]+$", identity);
            Assert.DoesNotContain(tailMarker, identity, StringComparison.Ordinal);
            Assert.DoesNotContain(identity, static character => char.IsControl(character));
        });
        Assert.All(logger.Messages, message =>
        {
            Assert.True(message.Length <= 160);
            Assert.DoesNotContain(tailMarker, message, StringComparison.Ordinal);
            Assert.DoesNotContain(message, static character => char.IsControl(character));
            Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
        });
        Assert.Empty(logger.Exceptions);
    }

    [Fact]
    public async Task ListCallAndDisposeFailuresNeverAttachExceptionsOrMessagesToLogs()
    {
        var logger = new CapturingLogger<ExternalMcpConnectionManager>();
        var listClient = new RecordingClient
        {
            ListOverride = _ => throw new InvalidOperationException(Secret)
        };
        var listManager = CreateManager(new RecordingFactory(listClient), logger);
        await listManager.RefreshAsync(CancellationToken.None);
        await new ExternalMcpHostedService(listManager).DisposeAsync();

        var callClient = new RecordingClient
        {
            CallOverride = (_, _, _) => throw new InvalidOperationException(Secret)
        };
        var callManager = CreateManager(new RecordingFactory(callClient), logger);
        await callManager.RefreshAsync(CancellationToken.None);
        await callManager.CallToolAsync(Tool(), null, CancellationToken.None);
        await new ExternalMcpHostedService(callManager).DisposeAsync();

        var disposeClient = new RecordingClient
        {
            DisposeOverride = () => throw new InvalidOperationException(Secret)
        };
        var disposeManager = CreateManager(new RecordingFactory(disposeClient), logger);
        await disposeManager.RefreshAsync(CancellationToken.None);
        await new ExternalMcpHostedService(disposeManager).DisposeAsync();

        var text = string.Join('|', logger.Messages);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.Empty(logger.Exceptions);
        Assert.All(logger.Messages, message => Assert.Contains(nameof(InvalidOperationException), message));
    }

    [Fact]
    public async Task BackgroundFailureIsObservedWithoutLoggingExceptionContent()
    {
        var logger = new CapturingLogger<ExternalMcpConnectionManager>();
        var manager = new ExternalMcpConnectionManager(
            new ThrowingOptions(),
            new RecordingFactory(new RecordingClient()),
            new AlwaysConnectMcpPolicy(),
            logger);
        var hosted = new ExternalMcpHostedService(manager);

        await hosted.StartAsync(CancellationToken.None);
        await hosted.DisposeAsync();

        var message = Assert.Single(logger.Messages);
        Assert.Contains(nameof(InvalidOperationException), message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, message, StringComparison.Ordinal);
        Assert.Empty(logger.Exceptions);
    }

    private static ExternalMcpConnectionManager CreateManager(
        IExternalMcpClientFactory factory,
        ILogger<ExternalMcpConnectionManager>? logger = null,
        ExternalMcpServerOptions? server = null)
    {
        return new ExternalMcpConnectionManager(
            Options.Create(new ExternalMcpOptions
            {
                ConnectOnStartup = false,
                RefreshInterval = TimeSpan.Zero,
                Servers = [server ?? Server()]
            }),
            factory,
            new AlwaysConnectMcpPolicy(),
            logger ?? NullLogger<ExternalMcpConnectionManager>.Instance);
    }

    private static IConfiguration Configuration(bool enabled)
    {
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GenAIPlatform:ExternalMcp:ConnectOnStartup"] = "true",
            ["GenAIPlatform:ExternalMcp:RefreshInterval"] = "00:00:00",
            ["GenAIPlatform:ExternalMcp:Servers:0:Name"] = "safe server",
            ["GenAIPlatform:ExternalMcp:Servers:0:Command"] = "fake",
            ["GenAIPlatform:ExternalMcp:Servers:0:Enabled"] = enabled.ToString()
        }).Build();
    }

    private static ExternalMcpServerOptions Server(string name = "safe server") => new() { Name = name, Command = "fake" };

    private static ExternalMcpToolSnapshot Tool(string serverName = "safe_server")
    {
        using var document = System.Text.Json.JsonDocument.Parse("{}");
        return new ExternalMcpToolSnapshot(
            serverName,
            "tool",
            "mcp_safe_server_tool",
            "test",
            "sha256:test",
            document.RootElement.Clone(),
            IsSchemaless: false,
            TimeSpan.FromSeconds(1));
    }

    private sealed class RecordingFactory(RecordingClient client) : IExternalMcpClientFactory
    {
        public Func<CancellationToken, Task<IExternalMcpClient>>? CreateOverride { get; init; }

        public int CreateCalls { get; private set; }

        public Task<IExternalMcpClient> CreateAsync(
            ExternalMcpServerOptions server,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            return CreateOverride?.Invoke(cancellationToken) ?? Task.FromResult<IExternalMcpClient>(client);
        }
    }

    private sealed class ThrowingOptions : IOptions<ExternalMcpOptions>
    {
        public ExternalMcpOptions Value => throw new InvalidOperationException(Secret);
    }

    private sealed class RecordingClient : IExternalMcpClient
    {
        public Func<ValueTask>? DisposeOverride { get; init; }

        public Func<CancellationToken, Task<IReadOnlyList<ExternalMcpToolDescriptor>>>? ListOverride { get; init; }

        public Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, Task<ExternalMcpToolCallResult>>? CallOverride { get; init; }

        public int DisposeCalls { get; private set; }

        public int CallCalls { get; private set; }

        public Task<IReadOnlyList<ExternalMcpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
        {
            return ListOverride?.Invoke(cancellationToken)
                ?? Task.FromResult<IReadOnlyList<ExternalMcpToolDescriptor>>([]);
        }

        public Task<ExternalMcpToolCallResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            CallCalls++;
            return CallOverride?.Invoke(toolName, arguments, cancellationToken)
                ?? Task.FromResult(ExternalMcpToolCallResult.Unavailable("not used"));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return DisposeOverride?.Invoke() ?? ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public List<Exception> Exceptions { get; } = [];

        public List<IReadOnlyDictionary<string, object?>> Fields { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                Fields.Add(fields.ToDictionary(static field => field.Key, static field => field.Value));
            }

            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
    }
}
