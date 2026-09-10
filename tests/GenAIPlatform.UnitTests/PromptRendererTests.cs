using GenAIPlatform.Application.Generation.Prompts.Rendering;
using GenAIPlatform.Application.Generation.Prompts.Templates;
using GenAIPlatform.Domain.Prompts;

namespace GenAIPlatform.UnitTests;

public sealed class PromptRendererTests
{
    [Fact]
    public async Task RenderActiveAsync_RendersConfiguredPromptAndMetadata()
    {
        var renderer = new PromptRenderer(new InMemoryPromptTemplateProvider());

        var rendered = await renderer.RenderActiveAsync(
            DirectChatPrompt.TemplateName,
            new Dictionary<string, string>
            {
                ["message"] = "Hello from the test."
            },
            CancellationToken.None);

        Assert.Contains(".NET GenAI platform starter kit", rendered.SystemMessage);
        Assert.Equal("Hello from the test.", rendered.UserMessage);
        Assert.Equal(DirectChatPrompt.TemplateName, rendered.Metadata.TemplateName);
        Assert.Equal("v1", rendered.Metadata.Version);
        Assert.Matches("^[a-f0-9]{64}$", rendered.Metadata.ContentHash);
    }

    [Fact]
    public async Task RenderActiveAsync_ThrowsWhenRequiredVariableIsMissing()
    {
        var renderer = new PromptRenderer(new InMemoryPromptTemplateProvider());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            renderer.RenderActiveAsync(
                DirectChatPrompt.TemplateName,
                new Dictionary<string, string>(),
                CancellationToken.None));
    }

    [Fact]
    public async Task RenderActiveAsync_DoesNotExpandPlaceholderTextFromVariableValues()
    {
        var template = PromptTemplateVersion.Create(
            "test-template",
            "v1",
            PromptTemplateStatus.Active,
            "System prompt.",
            "{{message}} {{context}}",
            ["message", "context"],
            DateTimeOffset.UnixEpoch);
        var renderer = new PromptRenderer(new SingleTemplateProvider(template));

        var rendered = await renderer.RenderActiveAsync(
            "test-template",
            new Dictionary<string, string>
            {
                ["message"] = "{{context}}",
                ["context"] = "safe context"
            },
            CancellationToken.None);

        Assert.Equal("{{context}} safe context", rendered.UserMessage);
    }

    [Fact]
    public async Task RenderActiveAsync_RendersSystemMessagePlaceholders()
    {
        var template = PromptTemplateVersion.Create(
            "test-template",
            "v1",
            PromptTemplateStatus.Active,
            "System prompt for {{tenant}}.",
            "{{message}}",
            ["tenant", "message"],
            DateTimeOffset.UnixEpoch);
        var renderer = new PromptRenderer(new SingleTemplateProvider(template));

        var rendered = await renderer.RenderActiveAsync(
            "test-template",
            new Dictionary<string, string>
            {
                ["tenant"] = "demo-tenant",
                ["message"] = "safe message"
            },
            CancellationToken.None);

        Assert.Equal("System prompt for demo-tenant.", rendered.SystemMessage);
        Assert.Equal("safe message", rendered.UserMessage);
    }

    [Fact]
    public async Task RenderActiveAsync_ThrowsWhenTemplateContainsUndeclaredPlaceholder()
    {
        var template = PromptTemplateVersion.Create(
            "test-template",
            "v1",
            PromptTemplateStatus.Active,
            "System prompt.",
            "{{message}} {{context}}",
            ["message"],
            DateTimeOffset.UnixEpoch);
        var renderer = new PromptRenderer(new SingleTemplateProvider(template));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            renderer.RenderActiveAsync(
                "test-template",
                new Dictionary<string, string>
                {
                    ["message"] = "safe message"
                },
                CancellationToken.None));

        Assert.Contains("undeclared variable placeholder", exception.Message);
        Assert.Contains("context", exception.Message);
    }

    private sealed class SingleTemplateProvider(PromptTemplateVersion template) : IPromptTemplateProvider
    {
        public Task<PromptTemplateVersion?> GetActiveVersionAsync(
            string templateName,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<PromptTemplateVersion?>(
                string.Equals(template.TemplateName, templateName, StringComparison.Ordinal)
                    ? template
                    : null);
        }
    }
}
