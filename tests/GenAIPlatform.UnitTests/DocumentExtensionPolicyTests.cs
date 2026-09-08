using System.Text;
using GenAIPlatform.Application.Knowledge.Documents;
using GenAIPlatform.Application.Knowledge.Embeddings;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.UnitTests;

public sealed class DocumentExtensionPolicyTests
{
    [Theory]
    [InlineData(".txt", true)]
    [InlineData(".md", true)]
    [InlineData(".log", false)]
    public Task Defaults_AgreeForUploadAndExtraction(string extension, bool allowed) =>
        VerifyAsync(new DocumentIngestionOptions(), extension, allowed);

    [Theory]
    [InlineData(".TXT", true)]
    [InlineData(".log", true)]
    [InlineData(".md", false)]
    [InlineData(".pdf", false)]
    public Task ConfiguredTextAliases_AgreeForUploadAndExtraction(string extension, bool allowed) =>
        VerifyAsync(new DocumentIngestionOptions { AllowedExtensions = [" .TXT ", " .LoG "] }, extension, allowed);

    [Theory]
    [InlineData("")]
    [InlineData("txt")]
    [InlineData(".")]
    [InlineData("../txt")]
    [InlineData(".text/file")]
    [InlineData(".tar.gz")]
    [InlineData(".t xt")]
    [InlineData(null)]
    public void MalformedAllowlist_IsRejected(string? extension)
    {
        var options = new DocumentIngestionOptions { AllowedExtensions = extension is null ? [] : [extension] };
        Assert.True(new DocumentIngestionOptionsValidator(Options.Create(new EmbeddingOptions())).Validate(null, options).Failed);
    }

    [Fact]
    public async Task ConfiguredAlias_StillRejectsInvalidUtf8()
    {
        var extractor = new PlainTextDocumentTextExtractor(Options.Create(
            new DocumentIngestionOptions { AllowedExtensions = [".log"] }));
        using var content = new MemoryStream([0xC3, 0x28]);
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            extractor.ExtractAsync(Document(".log"), content, TestContext.Current.CancellationToken));
    }

    private static async Task VerifyAsync(DocumentIngestionOptions options, string extension, bool allowed)
    {
        Assert.True(new DocumentIngestionOptionsValidator(Options.Create(new EmbeddingOptions())).Validate(null, options).Succeeded);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("Plain text alias."));
        var command = new UploadDocumentCommand("notes" + extension, "text/plain", content.Length, null, "Private", content);
        var validation = await new UploadDocumentValidator(Options.Create(options))
            .ValidateAsync(command, TestContext.Current.CancellationToken);
        Assert.Equal(allowed, validation.IsValid);
        var extractor = new PlainTextDocumentTextExtractor(Options.Create(options));
        if (allowed)
        {
            Assert.Equal("Plain text alias.", await extractor.ExtractAsync(Document(extension), content, TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<DocumentValidationException>(() =>
                extractor.ExtractAsync(Document(extension), content, TestContext.Current.CancellationToken));
        }
    }

    private static Document Document(string extension) => new(
        Guid.NewGuid(), "tenant", "owner", "notes" + extension, "Notes", "text/plain", extension,
        "memory://notes", 17, new string('a', 64), 1, DocumentAccessLevel.Private,
        DocumentIndexingStatus.PendingIndexing, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
}
