using System.Text;
using GenAIPlatform.Domain.Documents;
using Microsoft.Extensions.Options;

namespace GenAIPlatform.Application.Knowledge.Documents;

public sealed class PlainTextDocumentTextExtractor(IOptions<DocumentIngestionOptions> options) : ITextExtractor
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public async Task<string> ExtractAsync(
        Document document,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (!options.Value.AllowsExtension(document.SourceExtension))
        {
            throw new DocumentValidationException(
                $"Document extension '{document.SourceExtension}' is not supported for text extraction.");
        }

        using var reader = new StreamReader(
            content,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);

        try
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (DecoderFallbackException exception)
        {
            throw new DocumentValidationException(
                "Document text must be valid UTF-8.",
                exception);
        }
    }
}
