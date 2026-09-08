using GenAIPlatform.Application.Knowledge.Documents;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed partial class DocumentIngestionTests
{
    [Fact]
    public void TextChunker_CreatesStableChunksWithVersionMetadata()
    {
        var document = CreateDocument();
        var chunker = new TextChunker(Options.Create(new DocumentIngestionOptions
        {
            ChunkMaxCharacters = 80,
            ChunkOverlapCharacters = 12,
            ChunkingProfile = "test-profile",
            ChunkingProfileVersion = "v-test"
        }));
        var text = string.Join(' ', Enumerable.Range(1, 60).Select(static index => $"word{index}"));
        var now = DateTimeOffset.Parse("2026-05-09T12:00:00Z");

        var chunks = chunker.Chunk(document, text, now);
        var repeatedChunks = chunker.Chunk(document, text, now);

        Assert.True(chunks.Count > 1);
        Assert.Equal(
            chunks.Select(static chunk => chunk.Id),
            repeatedChunks.Select(static chunk => chunk.Id));
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(static chunk => chunk.Position));
        Assert.All(chunks, chunk =>
        {
            Assert.Equal(document.Id, chunk.DocumentId);
            Assert.Equal(document.Version, chunk.DocumentVersion);
            Assert.Equal("test-profile", chunk.ChunkingProfile);
            Assert.Equal("v-test", chunk.ChunkingProfileVersion);
            Assert.Matches("^[a-f0-9]{64}$", chunk.TextHash);
            Assert.True(chunk.ApproximateTokenCount > 0);
        });
    }

    [Fact]
    public void TextChunker_RespectsSmallConfiguredChunkSize()
    {
        var document = CreateDocument();
        var chunker = new TextChunker(Options.Create(new DocumentIngestionOptions
        {
            ChunkMaxCharacters = 40,
            ChunkOverlapCharacters = 0
        }));
        var text = new string('a', 95);

        var chunks = chunker.Chunk(
            document,
            text,
            DateTimeOffset.Parse("2026-05-09T12:00:00Z"));

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, chunk => Assert.True(chunk.Text.Length <= 40));
    }

    [Fact]
    public async Task PlainTextDocumentTextExtractor_RejectsInvalidUtf8()
    {
        var extractor = new PlainTextDocumentTextExtractor(Options.Create(new DocumentIngestionOptions()));
        await using var stream = new MemoryStream([0xC3, 0x28]);

        var exception = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            extractor.ExtractAsync(
                CreateDocument(),
                stream,
                CancellationToken.None));

        Assert.Equal("Document text must be valid UTF-8.", exception.Message);
    }

}
