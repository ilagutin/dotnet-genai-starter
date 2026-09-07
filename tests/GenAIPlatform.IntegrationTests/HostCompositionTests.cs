using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Core.ModelClients;
using GenAIPlatform.Application.Core.Security;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WorkerService = GenAIPlatform.Worker.Worker;

namespace GenAIPlatform.IntegrationTests;

[Collection<CurrentDirectorySensitiveCollection>]
public sealed class HostCompositionTests
{
    private static readonly SemaphoreSlim CurrentDirectoryLock = new(1, 1);

    public static IEnumerable<object[]> InvalidApplicationConfigurations =>
    [
        [new Dictionary<string, string?> { ["GenAIPlatform:Application:ApiVersion"] = " " }],
        [new Dictionary<string, string?> { ["GenAIPlatform:Application:RunnerVersion"] = "" }]
    ];

    public static IEnumerable<object[]> InvalidRagConfigurations =>
    [
        [new Dictionary<string, string?> { ["GenAIPlatform:Rag:DefaultTopK"] = "0" }],
        [
            new Dictionary<string, string?>
            {
                ["GenAIPlatform:Rag:DefaultTopK"] = "6",
                ["GenAIPlatform:Rag:MaxTopK"] = "5"
            }
        ],
        [new Dictionary<string, string?> { ["GenAIPlatform:Rag:MaxTopK"] = "51" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:Rag:DefaultMinSimilarityScore"] = "-1.01" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:Rag:DefaultMinSimilarityScore"] = "1.01" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:Rag:MaxDocumentFilters"] = "0" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:Rag:MaxDocumentFilters"] = "101" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:Rag:MaxContextCharacters"] = "499" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:Rag:MaxContextCharacters"] = "64001" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:Rag:NoContextFallbackMessage"] = " " }]
    ];

    public static IEnumerable<object[]> InvalidModelGatewayConfigurations =>
    [
        [new Dictionary<string, string?> { ["GenAIPlatform:ModelGateway:DefaultModel"] = "" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:ModelGateway:StrongModel"] = " " }],
        [new Dictionary<string, string?> { ["GenAIPlatform:ModelGateway:DefaultTemperature"] = "1.5" }],
        [
            new Dictionary<string, string?>
            {
                ["GenAIPlatform:ModelGateway:MinTemperature"] = "0.8",
                ["GenAIPlatform:ModelGateway:MaxTemperature"] = "0.7"
            }
        ],
        [new Dictionary<string, string?> { ["GenAIPlatform:ModelGateway:DefaultMaxOutputTokens"] = "0" }],
        [
            new Dictionary<string, string?>
            {
                ["GenAIPlatform:ModelGateway:DefaultMaxOutputTokens"] = "4096",
                ["GenAIPlatform:ModelGateway:MaxOutputTokensLimit"] = "2048"
            }
        ],
        [new Dictionary<string, string?> { ["GenAIPlatform:ModelGateway:MaxInputMessageCharacters"] = "0" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:ModelGateway:MaxCorrelationIdLength"] = "129" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:ModelGateway:AllowedModels:0"] = " " }]
    ];

    public static IEnumerable<object[]> InvalidDocumentIngestionConfigurations =>
    [
        [new Dictionary<string, string?> { ["GenAIPlatform:DocumentIngestion:MaxUploadBytes"] = "0" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:DocumentIngestion:MaxStorageCleanupAttempts"] = "0" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:DocumentIngestion:StorageCleanupRetryDelaySeconds"] = "-1" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:DocumentIngestion:MaxStorageCleanupRequestsPerPoll"] = "0" }],
        [new Dictionary<string, string?> { ["GenAIPlatform:DocumentIngestion:MaxStorageCleanupRequestsPerPoll"] = "51" }]
    ];

    public static IEnumerable<object[]> AcceptedProviderSpellings =>
    [
        ["Mock"],
        ["OpenAiCompatible"],
        ["OPENAI_COMPATIBLE"],
        ["OPENAI-COMPATIBLE"]
    ];

    [Fact]
    public void WorkerHostServices_CanBuildWithScopeValidation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GenAIPlatform:Application:ApiVersion"] = "v1",
                ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);
        services.AddScoped<IUserContext>(
            serviceProvider => serviceProvider.GetRequiredService<IBackgroundUserContext>());
        services.AddHostedService<WorkerService>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert.Single(provider.GetServices<IHostedService>());

        using var scope = provider.CreateScope();
        var backgroundContext = scope.ServiceProvider.GetRequiredService<IBackgroundUserContext>();
        var userContext = scope.ServiceProvider.GetRequiredService<IUserContext>();
        Assert.Same(backgroundContext, userContext);
        Assert.True(userContext.IsAuthenticated);
        Assert.Equal("system", userContext.UserId);
        Assert.Null(userContext.TenantId);
        Assert.Contains("system", userContext.Roles);
    }

    [Fact]
    public async Task HostServices_RejectUnsupportedModelGatewayProviderOnStart()
    {
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GenAIPlatform:ModelGateway:Provider"] = "TypoProvider"
                });
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
            })
            .Build();

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("unsupported", StringComparison.OrdinalIgnoreCase) &&
                       failure.Contains("TypoProvider", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AcceptedProviderSpellings))]
    public async Task HostServices_AcceptsDocumentedProviderSpellings(string provider)
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["GenAIPlatform:ModelGateway:Provider"] = provider,
            ["GenAIPlatform:ModelGateway:OpenAiCompatible:ApiKey"] = "test-api-key",
            ["GenAIPlatform:Embeddings:Provider"] = provider,
            ["GenAIPlatform:Embeddings:OpenAiCompatible:ApiKey"] = "test-api-key"
        });

        await host.StartAsync();

        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IAiModelClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEmbeddingClient>());
    }

    [Theory]
    [MemberData(nameof(InvalidApplicationConfigurations))]
    public async Task HostServices_RejectInvalidApplicationOptionsOnStart(
        IReadOnlyDictionary<string, string?> invalidConfiguration)
    {
        using var host = CreateHostWithConfiguration(invalidConfiguration);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        // [OptionsValidator] (source-generated) emits one failure per failing property, formatted
        // by the attribute's ErrorMessage. The invalidated field name appears in the failure
        // text, which is what we anchor the assertion on now.
        var expectedFieldName = invalidConfiguration.Keys.First().Split(':')[^1];
        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains(expectedFieldName, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(InvalidDocumentIngestionConfigurations))]
    public async Task HostServices_RejectInvalidDocumentIngestionOptionsOnStart(
        IReadOnlyDictionary<string, string?> invalidConfiguration)
    {
        using var host = CreateHostWithConfiguration(invalidConfiguration);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("Document ingestion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HostServices_RejectInvalidEmbeddingOptionsOnStart()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["GenAIPlatform:Embeddings:DefaultModel"] = ""
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        // [OptionsValidator] (source-generated) emits one failure per failing property; the
        // invalidated field name appears in the failure text.
        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("DefaultModel", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(InvalidModelGatewayConfigurations))]
    public async Task HostServices_RejectInvalidModelGatewayOptionsOnStart(
        IReadOnlyDictionary<string, string?> values)
    {
        using var host = CreateHostWithConfiguration(values);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("Model gateway configuration", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(InvalidRagConfigurations))]
    public async Task HostServices_RejectInvalidRagOptionsOnStart(
        IReadOnlyDictionary<string, string?> values)
    {
        using var host = CreateHostWithConfiguration(values);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("RAG configuration", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HostServices_RejectInvalidLocalDocumentStorageOptionsOnStart()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["GenAIPlatform:DocumentStorage:RootPath"] = ""
        });

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.NotNull(exception);
        Assert.Contains(
            GetOptionsValidationFailures(exception),
            failure => failure.Contains("Local document storage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MockEmbeddingClient_UsesConfiguredDimensions()
    {
        using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
        {
            ["GenAIPlatform:Embeddings:MockDimensions"] = "1024"
        });
        await host.StartAsync();

        var embeddingClient = host.Services.GetRequiredService<IEmbeddingClient>();
        var response = await embeddingClient.CreateEmbeddingAsync(
            new EmbeddingRequest("dimension test", "mock-embedding", CorrelationId: null),
            TestContext.Current.CancellationToken);

        Assert.Equal(1024, response.Vector.Count);
    }

    [Fact]
    public async Task LocalDocumentStorage_StagesAndRequiresCommitBeforeRead()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"genai-storage-tests-{Guid.NewGuid():n}");
        try
        {
            using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
            {
                ["GenAIPlatform:DocumentStorage:RootPath"] = rootPath
            });
            await host.StartAsync();

            var storage = host.Services.GetRequiredService<IDocumentStorage>();
            var committed = await storage.SaveAsync(
                Guid.NewGuid(),
                "notes.md",
                new MemoryStream("committed content"u8.ToArray()),
                maxSizeBytes: 1024,
                TestContext.Current.CancellationToken);

            var committedPath = Path.Combine(rootPath, committed.StoragePath);
            var stagedPath = Path.Combine(rootPath, committed.StagedStoragePath!);
            Assert.False(Path.IsPathFullyQualified(committed.StoragePath));
            Assert.False(Path.IsPathFullyQualified(committed.StagedStoragePath!));
            Assert.False(File.Exists(committedPath));
            Assert.True(File.Exists(stagedPath));
            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                storage.OpenReadAsync(committed.StoragePath, TestContext.Current.CancellationToken));

            await storage.CommitAsync(committed, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(committedPath));
            Assert.False(File.Exists(stagedPath));
            await using var recovered = await storage.OpenReadAsync(
                committed.StoragePath,
                TestContext.Current.CancellationToken);
            using var reader = new StreamReader(recovered);

            Assert.Equal("committed content", await reader.ReadToEndAsync());
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("size-limit")]
    [InlineData("cancellation")]
    [InlineData("io")]
    public async Task LocalDocumentStorage_RemovesPartialStagedFileWhenSaveFailsBeforeReturning(string failureMode)
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"genai-storage-tests-{Guid.NewGuid():n}");
        try
        {
            using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
            {
                ["GenAIPlatform:DocumentStorage:RootPath"] = rootPath
            });
            await host.StartAsync();

            var storage = host.Services.GetRequiredService<IDocumentStorage>();
            await using var stream = CreateFailingSaveStream(failureMode);

            var exception = await Record.ExceptionAsync(() =>
                storage.SaveAsync(
                    Guid.NewGuid(),
                    "partial.md",
                    stream,
                    maxSizeBytes: failureMode == "size-limit" ? 4 : 1024,
                    TestContext.Current.CancellationToken));

            AssertSaveFailureType(failureMode, exception);
            Assert.Empty(EnumerateFilesIfDirectoryExists(rootPath));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LocalDocumentStorage_StoredIdentityCanBeReadBySeparateStorageInstance()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"genai-storage-tests-{Guid.NewGuid():n}");
        try
        {
            using (var writerHost = CreateHostWithConfiguration(new Dictionary<string, string?>
            {
                ["GenAIPlatform:DocumentStorage:RootPath"] = rootPath
            }))
            {
                await writerHost.StartAsync();
                var writerStorage = writerHost.Services.GetRequiredService<IDocumentStorage>();
                var stored = await writerStorage.SaveAsync(
                    Guid.NewGuid(),
                    "portable.md",
                    new MemoryStream("portable content"u8.ToArray()),
                    maxSizeBytes: 1024,
                    TestContext.Current.CancellationToken);
                await writerStorage.CommitAsync(stored, TestContext.Current.CancellationToken);

                Assert.False(Path.IsPathFullyQualified(stored.StoragePath));

                using var readerHost = CreateHostWithConfiguration(new Dictionary<string, string?>
                {
                    ["GenAIPlatform:DocumentStorage:RootPath"] = rootPath
                });
                await readerHost.StartAsync();
                var readerStorage = readerHost.Services.GetRequiredService<IDocumentStorage>();

                await using var stream = await readerStorage.OpenReadAsync(
                    stored.StoragePath,
                    TestContext.Current.CancellationToken);
                using var reader = new StreamReader(stream);

                Assert.Equal("portable content", await reader.ReadToEndAsync());
            }
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LocalDocumentStorage_DefaultRelativeRootCanBeReadAcrossDifferentCurrentDirectories()
    {
        var originalCurrentDirectory = Environment.CurrentDirectory;
        var apiCurrentDirectory = Directory.CreateTempSubdirectory("genai-api-cwd-").FullName;
        var workerCurrentDirectory = Directory.CreateTempSubdirectory("genai-worker-cwd-").FullName;
        StoredDocument? stored = null;

        await CurrentDirectoryLock.WaitAsync();
        try
        {
            Environment.CurrentDirectory = apiCurrentDirectory;
            using var apiHost = CreateHostWithConfiguration(new Dictionary<string, string?>());
            await apiHost.StartAsync();
            var apiStorage = apiHost.Services.GetRequiredService<IDocumentStorage>();

            stored = await apiStorage.SaveAsync(
                Guid.NewGuid(),
                "default-root.md",
                new MemoryStream("default root content"u8.ToArray()),
                maxSizeBytes: 1024,
                TestContext.Current.CancellationToken);
            await apiStorage.CommitAsync(stored, TestContext.Current.CancellationToken);

            Assert.False(Path.IsPathFullyQualified(stored.StoragePath));

            Environment.CurrentDirectory = workerCurrentDirectory;
            using var workerHost = CreateHostWithConfiguration(new Dictionary<string, string?>());
            await workerHost.StartAsync();
            var workerStorage = workerHost.Services.GetRequiredService<IDocumentStorage>();

            await using (var stream = await workerStorage.OpenReadAsync(
                             stored.StoragePath,
                             TestContext.Current.CancellationToken))
            using (var reader = new StreamReader(stream))
            {
                Assert.Equal("default root content", await reader.ReadToEndAsync());
            }

            await workerStorage.DeleteAsync(stored.StoragePath, TestContext.Current.CancellationToken);
            stored = null;
        }
        finally
        {
            Environment.CurrentDirectory = originalCurrentDirectory;
            CurrentDirectoryLock.Release();

            if (stored is not null)
            {
                using var cleanupHost = CreateHostWithConfiguration(new Dictionary<string, string?>());
                await cleanupHost.StartAsync();
                var cleanupStorage = cleanupHost.Services.GetRequiredService<IDocumentStorage>();
                await cleanupStorage.DeleteAsync(stored.StoragePath, TestContext.Current.CancellationToken);
            }

            Directory.Delete(apiCurrentDirectory, recursive: true);
            Directory.Delete(workerCurrentDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task LocalDocumentStorage_RejectsReadAndDeleteOutsideConfiguredRoot()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), $"genai-storage-tests-{Guid.NewGuid():n}");
        var outsidePath = Path.Combine(Path.GetTempPath(), $"genai-outside-{Guid.NewGuid():n}.txt");
        try
        {
            await File.WriteAllTextAsync(outsidePath, "outside root");
            using var host = CreateHostWithConfiguration(new Dictionary<string, string?>
            {
                ["GenAIPlatform:DocumentStorage:RootPath"] = rootPath
            });
            await host.StartAsync();

            var storage = host.Services.GetRequiredService<IDocumentStorage>();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                storage.OpenReadAsync(outsidePath, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                storage.DeleteAsync(outsidePath, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(outsidePath));
        }
        finally
        {
            if (File.Exists(outsidePath))
            {
                File.Delete(outsidePath);
            }

            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    private static IHost CreateHostWithConfiguration(
        IReadOnlyDictionary<string, string?> values)
    {
        return new HostBuilder()
            .ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(values);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddTestApplication(context.Configuration);
                services.AddInfrastructure(context.Configuration);
            })
            .Build();
    }

    private static Stream CreateFailingSaveStream(string failureMode)
    {
        return failureMode switch
        {
            "size-limit" => new MemoryStream("content exceeds limit"u8.ToArray()),
            "cancellation" => new FailingAfterFirstReadStream(
                "part"u8.ToArray(),
                new OperationCanceledException("Storage copy was canceled.")),
            "io" => new FailingAfterFirstReadStream(
                "part"u8.ToArray(),
                new IOException("Storage copy failed.")),
            _ => throw new ArgumentOutOfRangeException(nameof(failureMode), failureMode, null)
        };
    }

    private static void AssertSaveFailureType(
        string failureMode,
        Exception? exception)
    {
        switch (failureMode)
        {
            case "size-limit":
                Assert.IsType<DocumentStorageLimitExceededException>(exception);
                break;
            case "cancellation":
                Assert.IsType<OperationCanceledException>(exception);
                break;
            case "io":
                Assert.IsType<IOException>(exception);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failureMode), failureMode, null);
        }
    }

    private static IReadOnlyCollection<string> EnumerateFilesIfDirectoryExists(string path)
    {
        return Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToArray()
            : [];
    }

    private static IEnumerable<string> GetOptionsValidationFailures(Exception exception)
    {
        if (exception is OptionsValidationException optionsValidationException)
        {
            return optionsValidationException.Failures;
        }

        if (exception is AggregateException aggregateException)
        {
            return aggregateException
                .Flatten()
                .InnerExceptions
                .OfType<OptionsValidationException>()
                .SelectMany(static optionsValidationException => optionsValidationException.Failures);
        }

        return [];
    }

    private sealed class FailingAfterFirstReadStream(
        byte[] firstRead,
        Exception exception)
        : Stream
    {
        private bool returnedFirstRead;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!returnedFirstRead)
            {
                returnedFirstRead = true;
                firstRead.CopyTo(buffer);
                return ValueTask.FromResult(firstRead.Length);
            }

            return ValueTask.FromException<int>(exception);
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count)
        {
            if (!returnedFirstRead)
            {
                returnedFirstRead = true;
                Array.Copy(firstRead, 0, buffer, offset, firstRead.Length);
                return firstRead.Length;
            }

            throw exception;
        }

        public override void Flush()
        {
        }

        public override long Seek(
            long offset,
            SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(
            byte[] buffer,
            int offset,
            int count)
        {
            throw new NotSupportedException();
        }
    }
}
