using GenAIPlatform.Application.Generation.Chat;
using GenAIPlatform.Application.Knowledge.Retrieval;

namespace GenAIPlatform.UnitTests;

public sealed class RagPromptBuilderTests
{
    [Fact]
    public void Build_SkipsBlankChunkAndKeepsLaterUsableContext()
    {
        var builder = new RagPromptBuilder();
        var skippedChunk = CreateChunk("Blank notes", "   ");
        var includedChunk = CreateChunk("Useful notes", "Useful context.");

        var context = builder.Build(
            [skippedChunk, includedChunk],
            maxContextCharacters: 500);

        var citation = Assert.Single(context.Citations);
        Assert.Equal("1", citation.ReferenceId);
        Assert.Equal(includedChunk.DocumentId, citation.DocumentId);
        Assert.DoesNotContain(skippedChunk.DocumentId.ToString("D"), context.ContextText);
        Assert.Contains("Useful context.", context.ContextText);
    }

    [Fact]
    public void Build_SkipsChunkWhosePrefixCannotFitAndKeepsLaterUsableContext()
    {
        var builder = new RagPromptBuilder();
        var skippedChunk = CreateChunk(new string('x', 240), "Skipped context.");
        var includedChunk = CreateChunk("Fit", "Later context.");

        var context = builder.Build(
            [skippedChunk, includedChunk],
            maxContextCharacters: 80);

        var citation = Assert.Single(context.Citations);
        Assert.Equal("1", citation.ReferenceId);
        Assert.Equal(includedChunk.ChunkId, citation.ChunkId);
        Assert.DoesNotContain(skippedChunk.ChunkId.ToString("D"), context.ContextText);
        Assert.Contains("Later context.", context.ContextText);
    }

    [Fact]
    public void Build_KeepsDurableIdentifiersOutOfPromptContextButInCitations()
    {
        var builder = new RagPromptBuilder();
        var chunk = CreateChunk("Secure notes", "Allowed context.");

        var context = builder.Build(
            [chunk],
            maxContextCharacters: 500);

        var citation = Assert.Single(context.Citations);
        Assert.Equal(chunk.DocumentId, citation.DocumentId);
        Assert.Equal(chunk.ChunkId, citation.ChunkId);
        Assert.Contains("[1]", context.ContextText);
        Assert.Contains("Secure notes", context.ContextText);
        Assert.Contains("Allowed context.", context.ContextText);
        Assert.DoesNotContain(chunk.DocumentId.ToString("D"), context.ContextText);
        Assert.DoesNotContain(chunk.ChunkId.ToString("D"), context.ContextText);
    }

    [Fact]
    public void Build_BoundsPromptMetadataAndKeepsSingleOverlongChunkUsable()
    {
        var builder = new RagPromptBuilder();
        var title = new string('t', 500);
        var fileName = new string('f', 500) + ".md";
        var chunk = CreateChunk(title, "Relevant context.", fileName);

        var context = builder.Build(
            [chunk],
            maxContextCharacters: 500);

        var citation = Assert.Single(context.Citations);
        Assert.Equal(title, citation.Title);
        Assert.Equal(fileName, citation.FileName);
        Assert.True(context.ContextText.Length <= 500);
        Assert.Contains("Relevant context.", context.ContextText);
        Assert.DoesNotContain(new string('t', 200), context.ContextText);
        Assert.DoesNotContain(new string('f', 200), context.ContextText);
    }

    private static RetrievedDocumentChunk CreateChunk(
        string title,
        string text,
        string fileName = "notes.md")
    {
        return new RetrievedDocumentChunk(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DocumentVersion: 1,
            ChunkPosition: 0,
            title,
            fileName,
            text,
            SimilarityScore: 0.9);
    }
}
