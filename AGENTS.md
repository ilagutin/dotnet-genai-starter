# Agent Contract

`dotnet-genai-starter` is a .NET-native GenAI Platform Starter Kit: a reference implementation, not a production system or stable framework. It demonstrates RAG, model gateway, prompts, permission-aware retrieval, logging, cost tracking, evaluations and backend-controlled tools.

Use `README.md` and relevant `docs/` files, especially architecture, code organization, security, RAG, model gateway, safe tools, trade-offs and versioning.

## Architecture

Keep a modular monolith. `Domain` depends on no Application module, Infrastructure, host, provider SDK or persistence library. Application references are exactly:

| Module | May reference |
|---|---|
| Core | Domain |
| Knowledge | Domain, Core |
| Generation | Domain, Core, Knowledge |
| Agentic | Domain, Core, Generation |
| Evaluations | Domain, Core, Knowledge, Generation |
| Usage | Domain, Core |

Application owns use cases, ports, orchestration, validation and pipeline behavior. Infrastructure implements persistence, pgvector retrieval, storage and model/embedding adapters; it owns sanitized logging and pricing/cost persistence. Api owns HTTP mapping, OpenAPI and foreground user context only. Worker dispatches its needed modules; Evaluations is a CLI host over them. Hosts compose explicit per-module registrations, never root `AddApplication`. Provider DTOs, HTTP, SQL and SDK concepts do not leak into Application or Domain.

## Current decisions

- .NET 10 LTS; API routes use `/api/v1/...`.
- Clean Architecture with simple Domain records, module-owned workflows and CQRS-lite feature/action folders, not separate read/write systems.
- Internal dispatcher, not required MediatR; FluentValidation runs in it and normalizers create values.
- Raw Npgsql provides PostgreSQL/pgvector; PostgreSQL-backed indexing jobs run in Worker.
- API/CLI use `IUserContext`; Worker/system use `IBackgroundUserContext`; header auth is demo-only.
- Mock providers are default; OpenAI-compatible adapters are replaceable Infrastructure adapters. API and CLI evaluations share services.
- The model proposes tool calls; backend policy decides execution.

## GenAI safety

- The LLM is not a security boundary. Filter tenant, owner, access level and metadata before prompt text.
- Never protect private data with prompts. Rendered-prompt logging is off by default; never log document text, prompts, credentials, connection strings, API keys or vectors.
- Tests do not call real model or embedding providers by default.
- Tool execution is deterministic backend behavior: validation, policy and approval state decide it. Unknown, forbidden or invalid calls fail closed and are audit-visible.

## Reliability

- Application ports are contracts. Normalize infrastructure exceptions at port boundaries into Application contracts; follow the Infrastructure Error Boundary.
- Reason about partial failure in storage, persistence, worker, lease, retry, provider, authorization and cleanup workflows. Do not report success after a required durable side effect fails without a documented, tested recovery invariant.
- Provider calls are cancelable or observed on shutdown, cancellation, stale lease or ownership loss; retry accounting reflects possible prior effects.
- Prefer fault-injection tests for commit/cleanup failure, provider timeout/retry/malformed responses, stale leases and duplicate workers. Run PostgreSQL/Testcontainers coverage for persistence, schema, retrieval, leases and migrations; report risk if Docker tests are skipped.

## Code organization

- Production classes stay under 200 physical lines unless a local exception is easier to defend than a split. Do not adopt BL-025 limits early.
- One class, record, struct, enum or interface per file.
- Use feature/action folders: `Command.cs` or `Query.cs`, `Handler.cs`, `Validator.cs`, `Normalizer.cs` when needed and `Response.cs`.
- Handlers orchestrate; parsing, rendering, policy, persistence and provider detail use named collaborators. Endpoints bind, dispatch and map only. Business validation belongs in validators/named policies; centralize public error/HTTP mapping and prefer typed options/constants and explicit names.

## Documentation and verification

Synchronize docs and README for setup, safety, provider behavior, API, persistence, evaluations or tool policy. Keep framing, trade-offs and release notes factual.

```powershell
dotnet restore --locked-mode GenAIPlatform.slnx
dotnet build GenAIPlatform.slnx
dotnet format GenAIPlatform.slnx --verify-no-changes --verbosity minimal
powershell -ExecutionPolicy Bypass -File scripts\code-organization-gate.ps1
powershell -ExecutionPolicy Bypass -File scripts\package-vulnerability-gate.ps1
dotnet test GenAIPlatform.slnx
$env:GENAI_REQUIRE_DOCKER_TESTS = "true"
dotnet test tests\GenAIPlatform.IntegrationTests\GenAIPlatform.IntegrationTests.csproj
```

## When unsure

Prefer existing architecture and trade-offs. Ask only when a choice changes architecture, versioning, security posture or public behavior; otherwise choose the conservative, understandable implementation.
