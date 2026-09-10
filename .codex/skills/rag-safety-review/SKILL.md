---
name: rag-safety-review
description: Review or modify dotnet-genai-starter RAG behavior safely. Use when working on RAG chat, retrieval, pgvector search, document access filters, no-context responses, citation rendering, prompt-budget behavior, RAG observability, or public documentation that describes RAG behavior.
---

# RAG Safety Review

Use this skill to keep RAG changes honest, secure, observable and easy to verify.

## First Read

Before changing non-trivial RAG behavior, read:

- `AGENTS.md`
- `docs/rag-pipeline.md`
- `docs/security-model.md`
- `docs/observability.md`
- `docs/code-organization.md`

If the change touches provider/model behavior, also read `docs/model-gateway.md`.
If the change touches pgvector schema, indexing or embedding dimensions, also read
`docs/trade-offs.md` and `docs/versioning.md`.

## Key Files

Use `rg` to confirm current file names before citing implementation details. Expected areas:

- `src/GenAIPlatform.Application.Generation/Chat/Rag/`
- `src/GenAIPlatform.Application.Generation/Chat/Rag/Pipeline/`
- `src/GenAIPlatform.Application.Knowledge/Retrieval/`
- `src/GenAIPlatform.Application.Knowledge/Documents/ProcessIndexingJobs/`
- `src/GenAIPlatform.Application.Knowledge/Embeddings/`
- `src/GenAIPlatform.Infrastructure/Retrieval/`
- `src/GenAIPlatform.Infrastructure/Embeddings/`
- `tests/GenAIPlatform.UnitTests/RagChatHandlerTests.cs`
- `tests/GenAIPlatform.UnitTests/RagPromptBuilderTests.cs`
- `tests/GenAIPlatform.IntegrationTests/`
- `infra/postgres/init/`

## Out Of Scope

This skill does not cover:

- model gateway internals, provider adapters, or model-selection policy;
- prompt template storage and versioning outside RAG context assembly;
- agentic chat tool execution, tool policy, budgets, or audit logging;
- evaluation pipeline and offline scoring workflows.

Those areas have separate concerns. Treat them through their own review path, not this skill.

## Non-Negotiable Invariants

Preserve these unless the user explicitly asks to redesign RAG semantics:

- Treat the LLM as untrusted for security decisions.
- Apply tenant, owner, access-level, requested-document and embedding provider/model filters before document text reaches prompt construction.
- Do not ask the model to hide unauthorized context. Do not retrieve unauthorized context and rely on prompt instructions.
- Return the configured no-context fallback before model completion when retrieval finds no usable context.
- Keep no-context responses explicit: no model provider, no token usage, no prompt metadata, `NoContext = true`, and no citations.
- Check retrieval readiness before creating a query embedding.
- Reject invalid RAG request shape before provider calls when possible.
- Keep citations limited to chunks actually included in the rendered prompt context, not every retrieved chunk.
- Keep prompt-context size bounded by both RAG context budget and model gateway input limits.
- Never log secret or large-value data. This includes rendered prompts, document text, embedding vectors, credentials, connection strings, and API keys.
- Keep real provider calls out of automated tests by default.

## Review Workflow

When modifying RAG code:

1. Identify the behavior being changed: retrieval, filtering, context budgeting, prompt rendering, no-context response, citations, logging, indexing, or embedding/provider metadata used by RAG.
2. Find the application-level contract first. Prefer changes in validators, policies, handlers or named services before touching endpoints or infrastructure adapters.
3. Verify that provider-specific DTOs and SQL details stay out of Application contracts.
4. Check whether the change affects public API shape, docs, samples or README behavior descriptions.
5. Update focused tests before broad tests when behavior changes.
6. Re-check docs after code changes; do not leave docs describing the old behavior.

## Test Expectations

For RAG behavior changes, prefer behavior tests that prove the system boundary:

- no-context result does not call `IAiModelClient`;
- retrieval readiness failure does not call embedding or chat providers;
- empty or invalid `documentIds` fail before embedding/search;
- unauthorized or filtered documents never appear in prompt context;
- prompt-budget trimming affects citations and prompt text consistently;
- citations map to rendered context in order and exclude skipped chunks;
- malformed embedding vectors fail before vector search or model calls;
- no-context logging stores no prompt metadata or retrieved document references;
- provider failures preserve application-level error codes where applicable.

For persistence or pgvector changes, add or update integration tests. If Docker-backed tests cannot run locally, state that residual risk clearly.

## Documentation Sync

Update the matching docs when behavior changes:

- `docs/rag-pipeline.md` for request flow, response shape, retrieval rules and no-context behavior.
- `docs/security-model.md` for access filtering or auth-related retrieval behavior.
- `docs/observability.md` for request logging, token/cost, retrieved-document references or no-model outcomes.
- `docs/trade-offs.md` for deliberate compromises, especially pgvector, prompt management or access-model limitations.
- the current release-notes file under `docs/` when public release scope changes.
- `README.md` when public quickstart, feature list or safety framing changes.

When updating README or docs, verify referenced file paths against the current worktree.

## Verification Commands

Use the smallest reliable command first, then broaden:

```powershell
dotnet test --project tests\GenAIPlatform.UnitTests\GenAIPlatform.UnitTests.csproj --filter "FullyQualifiedName~Rag"
dotnet test --project tests\GenAIPlatform.UnitTests\GenAIPlatform.UnitTests.csproj
dotnet build GenAIPlatform.slnx
```

For retrieval, pgvector, schema or persistence changes:

```powershell
$env:GENAI_REQUIRE_DOCKER_TESTS = "true"
dotnet test --project tests\GenAIPlatform.IntegrationTests\GenAIPlatform.IntegrationTests.csproj
```

Run broader checks when the change crosses public behavior, docs or multiple modules:

```powershell
dotnet test --solution GenAIPlatform.slnx
dotnet format GenAIPlatform.slnx --verify-no-changes --verbosity minimal
powershell -ExecutionPolicy Bypass -File scripts\code-organization-gate.ps1
```

## Stop And Ask

Ask before changing any of these:

- security semantics for document access;
- the public no-context response contract;
- whether full prompt or document text logging is allowed;
- real provider calls in automated tests;
- pgvector schema compatibility or migration strategy;
- public claims that make the starter kit sound production-ready.

## Keep It Practical

This is a reference implementation. Prefer explicit, reviewable backend rules over broad framework abstractions. Add an abstraction only when it protects a real boundary or removes meaningful duplication.
