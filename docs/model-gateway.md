# Model Gateway

Application use cases must not call provider SDKs directly. Model access goes through application-owned abstractions implemented by Infrastructure.

## Chat Client

```csharp
public interface IAiModelClient
{
    Task<AiModelResponse> CompleteAsync(
        AiModelRequest request,
        CancellationToken cancellationToken);
}
```

Implemented adapters:

- OpenAI-compatible client;
- mock/fake client for tests and demos.

Possible future adapters:

- Azure OpenAI;
- local OpenAI-compatible endpoints;
- Anthropic;
- Google Gemini.

## Embedding Client

```csharp
public interface IEmbeddingClient
{
    Task<EmbeddingResponse> CreateEmbeddingAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken);
}
```

Implemented adapters:

- OpenAI-compatible embedding client;
- mock embedding client.

Document indexing uses `IEmbeddingClient` through the Application layer. The mock provider is the default for local development and tests; the OpenAI-compatible provider uses the configured embeddings endpoint, model, timeout and retry settings.

## Requirements

- Model name is configurable.
- Timeout is configurable.
- Retry policy is supported or explicitly planned.
- Token usage is captured when returned by the provider.
- Provider errors are normalized into application-level error types.
- Request/response objects carry correlation IDs.
- OpenAI-compatible chat completions include one `Idempotency-Key` header per
  high-level model request and reuse it across retry attempts.
- Tests use mock clients by default.
- OpenAI-compatible provider URLs use HTTPS by default; insecure HTTP is allowed only for explicit loopback development configuration.

Provider token usage remains nullable and is passed through without fabricating
missing counts. Direct and RAG calls may therefore succeed without usage. The
agentic loop applies a stricter boundary because it must decide whether tools or
another model call may run: missing usage stops with `UsageUnavailable`, while
invalid or overflowing counts stop with `InvalidUsage`. Valid totals may be
derived internally from complete components, but provider `Usage.TotalTokens`
is preserved. See [agentic limits](agentic-chat.md#limits) for the validation and
aggregation rules.

Token counts are provider-reported measurements; costs from pricing records or
the agentic local fallback are estimates. Unknown usage is not measured zero.
Stopping after a response prevents subsequent work but cannot undo the already
incurred in-flight model call or guarantee a hard external spending cap.

## Routing

Routing is configuration-driven:

- default model;
- strong model;
- cheap model;
- evaluation model;
- tool planning model.
