using System.Text.Json;
using GenAIPlatform.Domain.Agentic;
using GenAIPlatform.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class ExternalMcpAgentToolSourceTests
{
    [Fact]
    public void BuildPrefixedToolName_UsesProviderSafeAsciiAndStableMaxLength()
    {
        var name = ExternalMcpNameSanitizer.BuildPrefixedToolName(
            "Админ Server With A Very Very Long Name That Should Be Shortened",
            "Run SQL Query With A Very Very Long Name That Should Also Be Shortened");

        Assert.True(name.Length <= ExternalMcpNameSanitizer.MaxToolNameLength);
        Assert.Matches("^[a-z0-9_]+$", name);
        Assert.StartsWith("mcp_server_with_a_very", name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_UsesConfigOrderToolSortAllowListAndSafeMetadata()
    {
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Beta Server", FakeExternalMcpClient.WithTools(
            Tool("zeta", "ignored", "{}"),
            Tool("A Tool", "line\r\nwith\tcontrols", Schema())));
        factory.SetClient("Alpha", FakeExternalMcpClient.WithTools(
            Tool("allowed", new string('x', ExternalMcpDescriptionSanitizer.MaxLength + 20), Schema()),
            Tool("blocked", "not allow-listed", Schema())));
        var manager = CreateManager(factory,
            Server("Beta Server"),
            Server("Alpha", allowedTools: ["allowed"]));

        await manager.RefreshAsync(CancellationToken.None);

        var tools = new ExternalMcpAgentToolSource(manager).GetAvailableTools();

        Assert.Equal(["mcp_beta_server_a_tool", "mcp_beta_server_zeta", "mcp_alpha_allowed"],
            tools.Select(static tool => tool.Definition.Name).ToArray());
        Assert.All(tools, static tool =>
        {
            Assert.Equal(ToolRisk.Risky, tool.Policy.Risk);
            Assert.True(tool.Policy.RequiresApproval);
            Assert.StartsWith("sha256:", tool.Definition.SchemaVersion, StringComparison.Ordinal);
        });
        Assert.DoesNotContain('\r', tools[0].Definition.Description);
        Assert.DoesNotContain('\n', tools[0].Definition.Description);
        Assert.True(tools[2].Definition.Description.Length <= ExternalMcpDescriptionSanitizer.MaxLength);
    }

    [Fact]
    public async Task RefreshAsync_KeepsDefinitionSnapshotCapturedAtConnect()
    {
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "first definition", Schema("first")));
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManager(factory, Server("Server"));
        await manager.RefreshAsync(CancellationToken.None);
        var source = new ExternalMcpAgentToolSource(manager);
        var first = Assert.Single(source.GetAvailableTools()).Definition;

        client.Tools = [Tool("echo", "changed definition", Schema("changed"))];
        var second = Assert.Single(source.GetAvailableTools()).Definition;

        Assert.Equal("first definition", second.Description);
        Assert.Equal(first.SchemaVersion, second.SchemaVersion);
        Assert.Contains("first", second.InputSchema.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshAsync_OmitsMissingSchemaUnlessExactSchemalessExceptionIsConfigured()
    {
        var omittedClient = FakeExternalMcpClient.WithTools(
            new ExternalMcpToolDescriptor("echo", "Echo.", InputSchema: null));
        var omittedFactory = new FakeExternalMcpClientFactory();
        omittedFactory.SetClient("Server", omittedClient);
        var omittedManager = CreateManager(omittedFactory, Server("Server", allowedTools: ["echo"]));
        await omittedManager.RefreshAsync(CancellationToken.None);

        Assert.Empty(new ExternalMcpAgentToolSource(omittedManager).GetAvailableTools());

        var allowedClient = FakeExternalMcpClient.WithTools(
            new ExternalMcpToolDescriptor("echo", "Echo.", InputSchema: null));
        var allowedFactory = new FakeExternalMcpClientFactory();
        allowedFactory.SetClient("Server", allowedClient);
        var allowedManager = CreateManager(
            allowedFactory,
            Server("Server", allowedTools: ["echo"], schemalessTools: ["echo"]));
        await allowedManager.RefreshAsync(CancellationToken.None);

        var tool = Assert.Single(new ExternalMcpAgentToolSource(allowedManager).GetAvailableTools());
        Assert.Equal("object", tool.Definition.InputSchema.GetProperty("type").GetString());
        Assert.True(tool.Policy.RequiresApproval);
        Assert.True(Assert.Single(Assert.Single(allowedManager.GetSnapshots()).Tools).IsSchemaless);
    }

    [Fact]
    public async Task RefreshAsync_SchemalessFlagParticipatesInSnapshotHashAndNeverMasksPresentSchema()
    {
        var missingClient = FakeExternalMcpClient.WithTools(
            new ExternalMcpToolDescriptor("echo", "Echo.", InputSchema: null));
        var missingFactory = new FakeExternalMcpClientFactory();
        missingFactory.SetClient("Server", missingClient);
        var missingManager = CreateManager(
            missingFactory,
            Server("Server", allowedTools: ["echo"], schemalessTools: ["echo"]));
        await missingManager.RefreshAsync(CancellationToken.None);
        var schemaless = Assert.Single(Assert.Single(missingManager.GetSnapshots()).Tools);

        var presentClient = FakeExternalMcpClient.WithTools(Tool("echo", "Echo.", "{\"type\":\"object\"}"));
        var presentFactory = new FakeExternalMcpClientFactory();
        presentFactory.SetClient("Server", presentClient);
        var presentManager = CreateManager(
            presentFactory,
            Server("Server", allowedTools: ["echo"], schemalessTools: ["echo"]));
        await presentManager.RefreshAsync(CancellationToken.None);
        var present = Assert.Single(Assert.Single(presentManager.GetSnapshots()).Tools);

        Assert.True(schemaless.IsSchemaless);
        Assert.False(present.IsSchemaless);
        Assert.NotEqual(schemaless.SnapshotHash, present.SnapshotHash);

        var malformedClient = FakeExternalMcpClient.WithTools(Tool("echo", "Echo.", "[]"));
        var malformedFactory = new FakeExternalMcpClientFactory();
        malformedFactory.SetClient("Server", malformedClient);
        var malformedManager = CreateManager(
            malformedFactory,
            Server("Server", allowedTools: ["echo"], schemalessTools: ["echo"]));
        await malformedManager.RefreshAsync(CancellationToken.None);
        var malformed = Assert.Single(Assert.Single(malformedManager.GetSnapshots()).Tools);

        Assert.False(malformed.IsSchemaless);
        Assert.Equal(JsonValueKind.Array, malformed.InputSchema.ValueKind);
    }

    [Fact]
    public void Validate_RejectsBlankDuplicateCaseMismatchedAndInvalidSubsetSchemalessConfiguration()
    {
        var servers = new[]
        {
            Server("Blank", schemalessTools: [" "]),
            Server("Duplicate", schemalessTools: ["echo", "echo"]),
            Server("CaseMismatch", allowedTools: ["Echo"], schemalessTools: ["echo"]),
            Server("Outside", allowedTools: ["lookup"], schemalessTools: ["echo"])
        };
        var validator = new ExternalMcpOptionsValidator();

        foreach (var server in servers)
        {
            var result = validator.Validate(null, new ExternalMcpOptions { Servers = [server] });
            Assert.True(result.Failed);
        }
    }

    [Fact]
    public void Validate_EnforcesBoundedResultLimitRange()
    {
        var validator = new ExternalMcpOptionsValidator();

        var belowMinimum = validator.Validate(null, new ExternalMcpOptions
        {
            MaxToolResultBytes = ExternalMcpToolResultMapper.MinimumResultBytes - 1
        });
        var minimum = validator.Validate(null, new ExternalMcpOptions
        {
            MaxToolResultBytes = ExternalMcpToolResultMapper.MinimumResultBytes
        });
        var maximum = validator.Validate(null, new ExternalMcpOptions
        {
            MaxToolResultBytes = ExternalMcpOptions.MaximumToolResultBytes
        });
        var aboveMaximum = validator.Validate(null, new ExternalMcpOptions
        {
            MaxToolResultBytes = ExternalMcpOptions.MaximumToolResultBytes + 1
        });

        Assert.True(belowMinimum.Failed);
        Assert.Same(Microsoft.Extensions.Options.ValidateOptionsResult.Success, minimum);
        Assert.Same(Microsoft.Extensions.Options.ValidateOptionsResult.Success, maximum);
        Assert.True(aboveMaximum.Failed);
        Assert.Equal(32 * 1024, new ExternalMcpOptions().MaxToolResultBytes);
    }

    [Fact]
    public async Task RefreshAsync_DisposesClientWhenToolSnapshotListingFails()
    {
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        client.ListToolsAsyncOverride = _ => throw new InvalidOperationException("list failed");
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManager(factory, Server("Server"));

        await manager.RefreshAsync(CancellationToken.None);

        Assert.True(client.Disposed);
        Assert.Empty(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
    }

    [Fact]
    public async Task RefreshAsync_ConnectsServersInParallelAndPreservesConfigOrder()
    {
        // Both servers must reach the barrier before either returns; if Refresh connected
        // sequentially the first would wait alone, time out, and the assertion would fail.
        using var bothConnecting = new CountdownEvent(2);
        var beta = FakeExternalMcpClient.WithTools(Tool("b", "Beta tool.", Schema()));
        beta.ListToolsAsyncOverride = async _ =>
        {
            await Task.Yield();
            bothConnecting.Signal();
            if (!bothConnecting.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("External MCP connects ran sequentially.");
            }

            return beta.Tools;
        };
        var alpha = FakeExternalMcpClient.WithTools(Tool("a", "Alpha tool.", Schema()));
        alpha.ListToolsAsyncOverride = async _ =>
        {
            await Task.Yield();
            bothConnecting.Signal();
            if (!bothConnecting.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("External MCP connects ran sequentially.");
            }

            return alpha.Tools;
        };
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Beta", beta);
        factory.SetClient("Alpha", alpha);
        var manager = CreateManager(factory, Server("Beta"), Server("Alpha"));

        await manager.RefreshAsync(CancellationToken.None);

        var tools = new ExternalMcpAgentToolSource(manager)
            .GetAvailableTools()
            .Select(static tool => tool.Definition.Name)
            .ToArray();
        Assert.Equal(["mcp_beta_b", "mcp_alpha_a"], tools);
    }

    [Fact]
    public async Task RefreshAsync_RecoversServerThatWasUnavailableOnFirstAttempt()
    {
        var working = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        var factory = new FakeExternalMcpClientFactory();
        factory.EnqueueFailure("Server");
        factory.EnqueueClient("Server", working);
        var manager = CreateManager(factory, Server("Server"));

        await manager.RefreshAsync(CancellationToken.None);
        Assert.Empty(new ExternalMcpAgentToolSource(manager).GetAvailableTools());

        await manager.RefreshAsync(CancellationToken.None);

        Assert.Single(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
        Assert.Equal(2, factory.CreateCount("Server"));
    }

    [Fact]
    public async Task RefreshAsync_LeavesAvailableServerUntouchedOnSecondPass()
    {
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManager(factory, Server("Server"));

        await manager.RefreshAsync(CancellationToken.None);
        await manager.RefreshAsync(CancellationToken.None);

        Assert.Single(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
        Assert.Equal(1, factory.CreateCount("Server"));
    }

    [Fact]
    public async Task RefreshAsync_DoesNotAttemptConnectWhenPolicyDenies()
    {
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManagerCore(factory, connectOnStartup: true, refreshInterval: TimeSpan.Zero,
            new DenyAllMcpPolicy(), Server("Server"));

        await manager.RefreshAsync(CancellationToken.None);

        Assert.Empty(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
        Assert.Equal(0, factory.CreateCount("Server"));
    }

    [Fact]
    public async Task StartAsync_RunsWarmupInBackgroundWhenEnabled()
    {
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManagerCore(factory, connectOnStartup: true, refreshInterval: TimeSpan.Zero,
            new AlwaysConnectMcpPolicy(), Server("Server"));

        await new ExternalMcpHostedService(manager).StartAsync(CancellationToken.None);
        await manager.BackgroundActivity;

        Assert.Single(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
        Assert.Equal(1, factory.CreateCount("Server"));
    }

    [Fact]
    public async Task StartAsync_SkipsWarmupWhenConnectOnStartupDisabled()
    {
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManagerCore(factory, connectOnStartup: false, refreshInterval: TimeSpan.Zero,
            new AlwaysConnectMcpPolicy(), Server("Server"));

        await new ExternalMcpHostedService(manager).StartAsync(CancellationToken.None);
        await manager.BackgroundActivity;

        Assert.Empty(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
        Assert.Equal(0, factory.CreateCount("Server"));
    }

    [Fact]
    public async Task StartAsync_DoesNotBlockOnSlowConnect()
    {
        var release = new TaskCompletionSource();
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        client.ListToolsAsyncOverride = async cancellationToken =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return client.Tools;
        };
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManagerCore(factory, connectOnStartup: true, refreshInterval: TimeSpan.Zero,
            new AlwaysConnectMcpPolicy(), Server("Server"));

        await new ExternalMcpHostedService(manager).StartAsync(CancellationToken.None);

        Assert.False(manager.BackgroundActivity.IsCompleted);
        Assert.Empty(new ExternalMcpAgentToolSource(manager).GetAvailableTools());

        release.SetResult();
        await manager.BackgroundActivity;
        Assert.Single(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
    }

    [Fact]
    public async Task ExecuteAsync_RoundTripsNestedJsonArgumentsToSdkShape()
    {
        var client = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        client.CallResult = new ExternalMcpToolCallResult(
            IsError: false,
            JsonSerializer.SerializeToElement(new { ok = true }),
            ErrorMessage: null);
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManager(factory, Server("Server"));
        await manager.RefreshAsync(CancellationToken.None);
        var tool = Assert.Single(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
        using var arguments = JsonDocument.Parse("""
        {
          "text": "hello",
          "nested": { "count": 3, "flags": [ true, false ], "missing": null },
          "items": [ { "name": "a" }, { "name": "b" } ]
        }
        """);

        var validation = tool.Validate(arguments.RootElement);
        var result = await tool.ExecuteAsync(validation.SanitizedArguments, CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        var captured = JsonSerializer.SerializeToElement(client.CapturedArguments);
        Assert.Equal("hello", captured.GetProperty("text").GetString());
        Assert.Equal(3, captured.GetProperty("nested").GetProperty("count").GetInt32());
        Assert.True(captured.GetProperty("nested").GetProperty("flags")[0].GetBoolean());
        Assert.Equal(JsonValueKind.Null, captured.GetProperty("nested").GetProperty("missing").ValueKind);
        Assert.Equal("b", captured.GetProperty("items")[1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailedOnTimeoutAndMarksServerUnavailable()
    {
        var client = FakeExternalMcpClient.WithTools(Tool("slow", "Slow tool.", Schema()));
        client.CallAsync = async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ExternalMcpToolCallResult(false, JsonSerializer.SerializeToElement(new { ok = true }), null);
        };
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManager(factory, Server("Server", timeoutSeconds: 0.01));
        await manager.RefreshAsync(CancellationToken.None);
        var tool = Assert.Single(new ExternalMcpAgentToolSource(manager).GetAvailableTools());

        var timeout = await tool.ExecuteAsync(EmptyObject(), CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, timeout.Status);
        Assert.Equal("mcp_tool_timeout", timeout.ErrorCode);
        Assert.True(client.Disposed);
        Assert.Empty(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailedOnCallerCancellation()
    {
        var client = FakeExternalMcpClient.WithTools(Tool("slow", "Slow tool.", Schema()));
        client.CallAsync = async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ExternalMcpToolCallResult(false, JsonSerializer.SerializeToElement(new { ok = true }), null);
        };
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManager(factory, Server("Server", timeoutSeconds: 30));
        await manager.RefreshAsync(CancellationToken.None);
        var tool = Assert.Single(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        var cancellation = await tool.ExecuteAsync(EmptyObject(), canceled.Token);

        Assert.Equal(ToolExecutionStatus.Failed, cancellation.Status);
        Assert.Equal("mcp_tool_canceled", cancellation.ErrorCode);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFailedForRemoteErrorAndUnavailableServerIsNotListed()
    {
        var unavailableFactory = new FakeExternalMcpClientFactory();
        var unavailableManager = CreateManager(unavailableFactory, Server("Missing"));
        await unavailableManager.RefreshAsync(CancellationToken.None);

        Assert.Empty(new ExternalMcpAgentToolSource(unavailableManager).GetAvailableTools());

        var client = FakeExternalMcpClient.WithTools(Tool("fails", "Fails remotely.", Schema()));
        client.CallResult = new ExternalMcpToolCallResult(
            IsError: true,
            JsonSerializer.SerializeToElement(new { failed = true }),
            "remote failure");
        var factory = new FakeExternalMcpClientFactory();
        factory.SetClient("Server", client);
        var manager = CreateManager(factory, Server("Server"));
        await manager.RefreshAsync(CancellationToken.None);
        var tool = Assert.Single(new ExternalMcpAgentToolSource(manager).GetAvailableTools());

        var result = await tool.ExecuteAsync(EmptyObject(), CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, result.Status);
        Assert.Equal("mcp_tool_error", result.ErrorCode);
        Assert.Equal("remote failure", result.ErrorMessage);
    }

    [Fact]
    public async Task ExecuteAsync_ReconnectsAfterFailedCallAndStopDisposesConnections()
    {
        var firstClient = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        firstClient.CallAsync = (_, _, _) => throw new InvalidOperationException("server died");
        var secondClient = FakeExternalMcpClient.WithTools(Tool("echo", "Echoes input.", Schema()));
        secondClient.CallResult = new ExternalMcpToolCallResult(
            IsError: false,
            JsonSerializer.SerializeToElement(new { reconnected = true }),
            ErrorMessage: null);
        var factory = new FakeExternalMcpClientFactory();
        factory.EnqueueClient("Server", firstClient);
        factory.EnqueueClient("Server", secondClient);
        var manager = CreateManager(factory, Server("Server"));
        await manager.RefreshAsync(CancellationToken.None);
        var tool = Assert.Single(new ExternalMcpAgentToolSource(manager).GetAvailableTools());

        var result = await tool.ExecuteAsync(EmptyObject(), CancellationToken.None);
        await new ExternalMcpHostedService(manager).StopAsync(CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Succeeded, result.Status);
        Assert.True(result.Output.GetProperty("reconnected").GetBoolean());
        Assert.Equal(2, factory.CreateCount("Server"));
        Assert.True(firstClient.Disposed);
        Assert.True(secondClient.Disposed);
        Assert.Empty(new ExternalMcpAgentToolSource(manager).GetAvailableTools());
    }

    [Fact]
    public void ToSdkArguments_PreservesNestedShapeAndNumericFidelity()
    {
        using var document = JsonDocument.Parse("""
        {
          "bigInt": 9007199254740993,
          "highPrecision": 12345678901234567890.12345678901234567890,
          "negative": -42,
          "nested": { "flags": [ true, false ], "missing": null },
          "text": "hello"
        }
        """);

        var sdkArguments = ExternalMcpJsonRoundTrip.ToSdkArguments(document.RootElement);

        // The MCP SDK re-serializes these arguments via System.Text.Json. Assert the round-trip
        // preserves nested shape and raw numeric tokens (no lossy double/long conversion) — a
        // hand-rolled JsonElement mapper would break this.
        var reserialized = JsonSerializer.SerializeToElement(sdkArguments);
        Assert.Equal("9007199254740993", reserialized.GetProperty("bigInt").GetRawText());
        Assert.Equal(
            "12345678901234567890.12345678901234567890",
            reserialized.GetProperty("highPrecision").GetRawText());
        Assert.Equal("-42", reserialized.GetProperty("negative").GetRawText());
        Assert.True(reserialized.GetProperty("nested").GetProperty("flags")[0].GetBoolean());
        Assert.Equal(JsonValueKind.Null, reserialized.GetProperty("nested").GetProperty("missing").ValueKind);
        Assert.Equal("hello", reserialized.GetProperty("text").GetString());
    }

    [Fact]
    public void CloneSchema_PreservesMissingAndPresentMalformedSchemaWithoutSynthesis()
    {
        Assert.Null(ExternalMcpJsonRoundTrip.CloneSchema(default(JsonElement)));
        Assert.Null(ExternalMcpJsonRoundTrip.CloneSchema(null));
        using var malformed = JsonDocument.Parse("[]");

        var clone = ExternalMcpJsonRoundTrip.CloneSchema(malformed.RootElement);

        Assert.NotNull(clone);
        Assert.Equal(JsonValueKind.Array, clone.Value.ValueKind);
    }

    private static ExternalMcpConnectionManager CreateManager(
        FakeExternalMcpClientFactory factory,
        params ExternalMcpServerOptions[] servers)
    {
        return CreateManagerCore(factory, connectOnStartup: true, refreshInterval: TimeSpan.Zero,
            new AlwaysConnectMcpPolicy(), servers);
    }

    private static ExternalMcpConnectionManager CreateManagerCore(
        FakeExternalMcpClientFactory factory,
        bool connectOnStartup,
        TimeSpan refreshInterval,
        IExternalMcpConnectionPolicy policy,
        params ExternalMcpServerOptions[] servers)
    {
        return new ExternalMcpConnectionManager(
            Options.Create(new ExternalMcpOptions
            {
                ConnectOnStartup = connectOnStartup,
                RefreshInterval = refreshInterval,
                Servers = servers.ToList()
            }),
            factory,
            policy,
            NullLogger<ExternalMcpConnectionManager>.Instance);
    }

    private static ExternalMcpServerOptions Server(
        string name,
        IReadOnlyCollection<string>? allowedTools = null,
        IReadOnlyCollection<string>? schemalessTools = null,
        double timeoutSeconds = 30)
    {
        return new ExternalMcpServerOptions
        {
            Name = name,
            Command = "fake",
            AllowedTools = allowedTools?.ToList() ?? [],
            SchemalessTools = schemalessTools?.ToList() ?? [],
            ToolCallTimeoutSeconds = timeoutSeconds
        };
    }

    private static ExternalMcpToolDescriptor Tool(string name, string? description, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return new ExternalMcpToolDescriptor(name, description, document.RootElement.Clone());
    }

    private static string Schema(string marker = "value") => $$"""
    {
      "type": "object",
      "properties": {
        "{{marker}}": { "type": "string" }
      },
      "additionalProperties": true
    }
    """;

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private sealed class DenyAllMcpPolicy : IExternalMcpConnectionPolicy
    {
        public bool ShouldAttemptConnect(string serverName) => false;

        public void RecordConnectSuccess(string serverName)
        {
        }

        public void RecordConnectFailure(string serverName)
        {
        }
    }

    private sealed class FakeExternalMcpClientFactory : IExternalMcpClientFactory
    {
        private readonly Dictionary<string, Queue<Func<IExternalMcpClient>>> clients = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> createCounts = new(StringComparer.Ordinal);

        public void SetClient(string serverName, IExternalMcpClient client)
        {
            clients[serverName] = new Queue<Func<IExternalMcpClient>>([() => client]);
        }

        public void EnqueueClient(string serverName, IExternalMcpClient client)
        {
            QueueFor(serverName).Enqueue(() => client);
        }

        public void EnqueueFailure(string serverName)
        {
            QueueFor(serverName).Enqueue(static () => throw new InvalidOperationException("Fake MCP connect failed."));
        }

        public int CreateCount(string serverName) => createCounts.GetValueOrDefault(serverName);

        public Task<IExternalMcpClient> CreateAsync(
            ExternalMcpServerOptions server,
            CancellationToken cancellationToken)
        {
            createCounts[server.Name] = createCounts.GetValueOrDefault(server.Name) + 1;
            if (!clients.TryGetValue(server.Name, out var queue) || queue.Count == 0)
            {
                throw new InvalidOperationException("No fake MCP client was configured.");
            }

            return Task.FromResult(queue.Dequeue().Invoke());
        }

        private Queue<Func<IExternalMcpClient>> QueueFor(string serverName)
        {
            if (!clients.TryGetValue(serverName, out var queue))
            {
                queue = new Queue<Func<IExternalMcpClient>>();
                clients[serverName] = queue;
            }

            return queue;
        }
    }

    private sealed class FakeExternalMcpClient : IExternalMcpClient
    {
        public IReadOnlyList<ExternalMcpToolDescriptor> Tools { get; set; } = [];

        public IReadOnlyDictionary<string, object?>? CapturedArguments { get; private set; }

        public ExternalMcpToolCallResult CallResult { get; set; } = new(
            IsError: false,
            JsonSerializer.SerializeToElement(new { ok = true }),
            ErrorMessage: null);

        public Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, Task<ExternalMcpToolCallResult>>? CallAsync { get; set; }

        public Func<CancellationToken, Task<IReadOnlyList<ExternalMcpToolDescriptor>>>? ListToolsAsyncOverride { get; set; }

        public bool Disposed { get; private set; }

        public static FakeExternalMcpClient WithTools(params ExternalMcpToolDescriptor[] tools)
        {
            return new FakeExternalMcpClient { Tools = tools };
        }

        public Task<IReadOnlyList<ExternalMcpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
        {
            return ListToolsAsyncOverride is null
                ? Task.FromResult(Tools)
                : ListToolsAsyncOverride(cancellationToken);
        }

        public Task<ExternalMcpToolCallResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken)
        {
            CapturedArguments = arguments;
            return CallAsync is null
                ? Task.FromResult(CallResult)
                : CallAsync(toolName, arguments, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
