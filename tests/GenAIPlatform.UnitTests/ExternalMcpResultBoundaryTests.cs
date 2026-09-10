using System.Text;
using System.Text.Json;
using GenAIPlatform.Infrastructure.Mcp;
using ModelContextProtocol.Protocol;

namespace GenAIPlatform.UnitTests;

public sealed class ExternalMcpResultBoundaryTests
{
    [Fact]
    public void Map_KeepsOnlyContentAndStructuredContentAsProviderNeutralJson()
    {
        var result = new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = "usable text",
                    Meta = new System.Text.Json.Nodes.JsonObject { ["secret"] = "sdk-meta" }
                }
            ],
            StructuredContent = Json("""{"value":7,"meta":{"business":"preserved"},"nested":{"ok":true}}"""),
            Meta = new System.Text.Json.Nodes.JsonObject { ["secret"] = "root-meta" }
        };

        var mapped = ExternalMcpToolResultMapper.Map(result, int.MaxValue);

        Assert.Equal("usable text", mapped.Payload.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(7, mapped.Payload.GetProperty("structuredContent").GetProperty("value").GetInt32());
        Assert.Equal(
            "preserved",
            mapped.Payload.GetProperty("structuredContent").GetProperty("meta").GetProperty("business").GetString());
        Assert.False(mapped.Payload.GetProperty("content")[0].TryGetProperty("_meta", out _));
        Assert.False(mapped.Payload.GetProperty("content")[0].TryGetProperty("meta", out _));
        Assert.False(mapped.Payload.TryGetProperty("_meta", out _));
        Assert.False(mapped.Payload.TryGetProperty("meta", out _));
        Assert.NotNull(mapped.PayloadMetadata);
        Assert.False(mapped.PayloadMetadata.Truncated);
    }

    [Fact]
    public void Map_AllowsExactLimitAndOmitsOneByteOver()
    {
        var sdkResult = Result("exact-boundary");
        var unrestricted = ExternalMcpToolResultMapper.Map(sdkResult, int.MaxValue);
        var exactBytes = Encoding.UTF8.GetByteCount(unrestricted.Payload.GetRawText());

        var exact = ExternalMcpToolResultMapper.Map(sdkResult, exactBytes);
        var over = ExternalMcpToolResultMapper.Map(sdkResult, exactBytes - 1);

        Assert.False(exact.PayloadMetadata!.Truncated);
        Assert.Equal(unrestricted.Payload.GetRawText(), exact.Payload.GetRawText());
        Assert.True(over.PayloadMetadata!.Truncated);
        Assert.True(over.Payload.GetProperty("contentOmitted").GetBoolean());
        Assert.Equal(exactBytes, over.PayloadMetadata.SourceUtf8Bytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(over.Payload.GetRawText()), over.PayloadMetadata.ReturnedUtf8Bytes);
        Assert.True(over.PayloadMetadata.ReturnedUtf8Bytes <= exactBytes - 1);
    }

    [Fact]
    public void Map_CountsMultibyteUtf8AndReturnsValidOmissionJson()
    {
        var sdkResult = Result("zażółć gęślą jaźń 🔐");
        var mapped = ExternalMcpToolResultMapper.Map(sdkResult, 30);

        Assert.True(mapped.PayloadMetadata!.Truncated);
        Assert.True(mapped.PayloadMetadata.SourceUtf8Bytes > sdkResult.Content[0].ToString()!.Length);
        Assert.Equal(JsonValueKind.Object, mapped.Payload.ValueKind);
        Assert.True(mapped.Payload.GetProperty("contentOmitted").GetBoolean());
    }

    private static CallToolResult Result(string text)
    {
        return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
