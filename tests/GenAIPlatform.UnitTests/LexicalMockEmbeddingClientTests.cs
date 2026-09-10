using GenAIPlatform.Application.Core.Embeddings;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Infrastructure;
using GenAIPlatform.Infrastructure.Embeddings.Mock;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class LexicalMockEmbeddingClientTests
{
    private const string Model = "mock-embedding";

    [Fact]
    public async Task CreateEmbeddingAsync_IsDeterministicForIdenticalText()
    {
        var client = Client();

        var first = await Embed(client, "Tide forecasting combines lunar gravity harmonics.");
        var second = await Embed(client, "Tide forecasting combines lunar gravity harmonics.");

        Assert.Equal(first.Vector, second.Vector);
        Assert.Equal(LexicalMockEmbeddingClient.ProviderName, first.Provider);
        Assert.Equal(Model, first.Model);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_IgnoresCaseAndPunctuationWhenTokenizing()
    {
        var client = Client();

        var plain = await Embed(client, "warp threads weft picks");
        var decorated = await Embed(client, "  WARP, threads;  Weft -- picks!  ");

        Assert.Equal(plain.Vector, decorated.Vector);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_ScoresSharedTokensFarAboveUnrelatedText()
    {
        var client = Client();

        var target = await Embed(client, "Permafrost borehole thermistors log ground temperature through active layer.");
        var overlapping = await Embed(client, "Which thermistors log permafrost borehole temperature?");
        var unrelated = await Embed(client, "Who invented pinhole cameras?");

        var overlappingScore = Cosine(target.Vector, overlapping.Vector);
        var unrelatedScore = Cosine(target.Vector, unrelated.Vector);

        Assert.True(overlappingScore > 0.6, $"overlapping score was {overlappingScore}");
        Assert.True(unrelatedScore < 0.05, $"unrelated score was {unrelatedScore}");
        Assert.True(overlappingScore - unrelatedScore > 0.5);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(256)]
    [InlineData(1024)]
    public async Task CreateEmbeddingAsync_HonorsConfiguredDimensions(int dimensions)
    {
        var response = await Embed(Client(dimensions), "Loom weaving interlaces warp threads.");

        Assert.Equal(dimensions, response.Vector.Count);
        EmbeddingVectorValidator.EnsureValidCosineVector(response);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_ReturnsAValidVectorWhenNoTokenSurvivesTokenizing()
    {
        var response = await Embed(Client(), "! ? . , ; a I");

        Assert.Equal(0, response.InputTokens);
        EmbeddingVectorValidator.EnsureValidCosineVector(response);
        Assert.Equal(1d, Cosine(response.Vector, response.Vector), 6);
    }

    [Fact]
    public async Task CreateEmbeddingAsync_TruncatesInputToTheConfiguredCharacterLimit()
    {
        var client = Client(maxInputCharacters: 20);

        var truncated = await Embed(client, "avalanche forecast bulletins grade slab instability");
        var prefix = await Embed(client, "avalanche forecast bu");

        Assert.Equal(prefix.Vector, truncated.Vector);
    }

    [Fact]
    public void HashVariantStaysTheDefaultAndKeepsItsProviderName()
    {
        using var provider = BuildServiceProvider(variant: null);

        var client = provider.GetRequiredService<IEmbeddingClient>();

        Assert.IsType<MockEmbeddingClient>(client);
    }

    [Fact]
    public async Task HashVariantVectorsStayUnchangedByTheLexicalVariant()
    {
        using var provider = BuildServiceProvider(variant: null);
        var client = provider.GetRequiredService<IEmbeddingClient>();

        var response = await Embed(client, "Tide forecasting combines lunar gravity harmonics.");

        Assert.Equal("mock", response.Provider);
        Assert.Equal(16, response.Vector.Count);
    }

    [Fact]
    public void LexicalVariantIsSelectedByConfiguration()
    {
        using var provider = BuildServiceProvider("Lexical");

        Assert.IsType<LexicalMockEmbeddingClient>(provider.GetRequiredService<IEmbeddingClient>());
    }

    [Fact]
    public void UnsupportedVariantFailsClosed()
    {
        using var provider = BuildServiceProvider("Semantic");

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IEmbeddingClient>());
    }

    private static ServiceProvider BuildServiceProvider(string? variant)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:GenAIPlatform"] = "Host=localhost;Port=5432;Database=unused;Username=genai;Password=unused",
            ["GenAIPlatform:Postgres:ConnectionStringName"] = "GenAIPlatform"
        };
        if (variant is not null)
        {
            settings["GenAIPlatform:Embeddings:MockVariant"] = variant;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTestApplication(configuration);
        services.AddInfrastructure(configuration);

        return services.BuildServiceProvider();
    }

    private static LexicalMockEmbeddingClient Client(
        int dimensions = 1024,
        int maxInputCharacters = 8000)
    {
        return new LexicalMockEmbeddingClient(Options.Create(new EmbeddingOptions
        {
            Provider = "Mock",
            MockVariant = "Lexical",
            MockDimensions = dimensions,
            MaxInputCharacters = maxInputCharacters
        }));
    }

    private static Task<EmbeddingResponse> Embed(IEmbeddingClient client, string input)
    {
        return client.CreateEmbeddingAsync(
            new EmbeddingRequest(input, Model, "test-correlation"),
            TestContext.Current.CancellationToken);
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double score = 0;
        for (var index = 0; index < left.Count; index++)
        {
            score += (double)left[index] * right[index];
        }

        return score;
    }
}
