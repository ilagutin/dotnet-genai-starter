# Quickstart

This guide runs the local mock-provider demo path. It does not call real model
or embedding providers unless you explicitly configure OpenAI-compatible
adapters.

## Prerequisites

- .NET 10 SDK.
- Docker, for local PostgreSQL + pgvector.
- PowerShell examples below assume Windows, but the same `dotnet` and
  `docker compose` commands work cross-platform with shell syntax changes.

## Local Configuration

```powershell
Copy-Item .env.example .env
$env:ConnectionStrings__GenAIPlatform = "Host=localhost;Port=5432;Database=genai_platform;Username=genai;Password=genai_dev_password"
```

The application reads normal .NET configuration sources. Runtime
`appsettings.json` files intentionally omit database credentials; supply
`ConnectionStrings__GenAIPlatform` through environment variables, user secrets,
command-line configuration or dotenv-aware tooling. The `.env` file is ignored
by Git and should stay machine-local; copying `.env.example` is useful for
Docker Compose, but plain `dotnet run` does not load `.env` automatically unless
your shell/tooling does that for you. Values in `.env.example` are local-only
Docker/demo placeholders.

## Build And Test

```powershell
dotnet restore GenAIPlatform.slnx
dotnet build GenAIPlatform.slnx
dotnet test --solution GenAIPlatform.slnx
```

PostgreSQL repository tests use Testcontainers. Outside CI they skip when Docker
is unavailable; in CI, or when `GENAI_REQUIRE_DOCKER_TESTS=true` is set,
Docker-backed tests are required and will fail instead of silently skipping.

## Run Locally

Start local infrastructure:

```powershell
docker compose up -d postgres
```

The local PostgreSQL image applies the init scripts under `infra/postgres/init`
when the Docker volume is first created, including pgvector setup, document
ingestion, retrieval, observability/cost tracking, evaluations, tool audit
logging and the durable document storage cleanup queue. If you are reusing an
older local Docker volume, recreate it with `docker compose down -v` or apply
the missing numbered SQL scripts manually. Legacy chunks with non-finite or
zero-magnitude embedding arrays are left without `embedding_vector` and will not
participate in retrieval; re-index those documents to regenerate valid vectors.

Local document storage defaults to
`GenAIPlatform:DocumentStorage:RootPath=storage/documents`, resolved from each
host's `AppContext.BaseDirectory`. That is useful for a single published host,
but it does not make separately launched API and Worker processes share files.
For the local two-process demo, set one matching absolute path in both terminals.
Docker Compose starts PostgreSQL only and cannot configure either host process.
Orphaned document storage cleanup requests are stored in PostgreSQL
(`genai.document_storage_cleanup_requests`) so API and Worker hosts do not need
a shared local cleanup journal. When using the local filesystem storage adapter
across multiple hosts, the document files themselves still need shared storage
or a replaceable storage adapter that both hosts can access.

Run the API from the root of your clone:

```powershell
$repositoryRoot = (Resolve-Path .).Path
Set-Location $repositoryRoot
$sharedStorage = Join-Path $repositoryRoot ".local\quickstart-storage\documents"
$env:ConnectionStrings__GenAIPlatform = "Host=localhost;Port=5432;Database=genai_platform;Username=genai;Password=genai_dev_password"
$env:GenAIPlatform__DocumentStorage__RootPath = $sharedStorage
$env:GenAIPlatform__ModelGateway__Provider = "Mock"
$env:GenAIPlatform__Embeddings__Provider = "Mock"
dotnet run --project src/GenAIPlatform.Api --no-build --launch-profile http
```

In a second terminal, run the background worker so uploaded documents are
indexed. Start from the same clone root and repeat the identical setup. PowerShell
environment variables do not carry into a new window:

```powershell
$repositoryRoot = (Resolve-Path .).Path
Set-Location $repositoryRoot
$sharedStorage = Join-Path $repositoryRoot ".local\quickstart-storage\documents"
$env:ConnectionStrings__GenAIPlatform = "Host=localhost;Port=5432;Database=genai_platform;Username=genai;Password=genai_dev_password"
$env:GenAIPlatform__DocumentStorage__RootPath = $sharedStorage
$env:GenAIPlatform__ModelGateway__Provider = "Mock"
$env:GenAIPlatform__Embeddings__Provider = "Mock"
dotnet run --project src/GenAIPlatform.Worker --no-build
```

Useful local endpoints:

- `GET http://localhost:5198/api/v1/health`
- `GET http://localhost:5198/api/v1/users/me`
- `POST http://localhost:5198/api/v1/chat/direct`
- `POST http://localhost:5198/api/v1/chat/rag`
- `POST http://localhost:5198/api/v1/documents`
- `GET http://localhost:5198/api/v1/documents/{documentId}`
- `GET http://localhost:5198/api/v1/usage?from=2026-05-01T00:00:00Z&to=2026-05-31T23:59:59Z`
- `POST http://localhost:5198/api/v1/evaluations/runs`
- `GET http://localhost:5198/api/v1/evaluations/runs/{runId}/summary`
- `POST http://localhost:5198/api/v1/chat/agentic`

Sample HTTP requests are available in
[src/GenAIPlatform.Api/GenAIPlatform.Api.http](../src/GenAIPlatform.Api/GenAIPlatform.Api.http)
and [samples/http/local-demo.http](../samples/http/local-demo.http). The local
demo file covers direct chat, document upload, RAG, usage, evaluations and agentic
chat. The mock embedding provider is deterministic but not semantic; for a local
RAG demo that reliably returns citations, the sample RAG request lowers
`minSimilarityScore`.

## Demo Identity

Demo identity can be supplied with headers:

```http
X-Demo-User-Id: alice
X-Demo-Tenant-Id: local
X-Demo-Roles: developer,admin
X-Demo-Groups: demo,engineering
```

Demo header auth is enabled by default only when the API runs in `Development`,
where missing headers fall back to the local `demo-user` defaults for the
quickstart. A `Production` API host always requires a real `IUserContext`
authentication adapter in the API composition root. Non-production demo
environments can explicitly opt in to demo headers; in that opt-in mode, default
identity, tenant, role and group values are disabled: a request without
`X-Demo-User-Id` remains unauthenticated and receives no default claims. Do not
use `X-Demo-*` headers as deployed authentication.

## Demo Requests

Direct chat uses the deterministic mock model provider by default, so local runs
and automated tests do not call a real LLM. If a request supplies `model`, the
value must match a backend-configured route (`default`, `strong`, `cheap`,
`evaluation`) or an allowed configured model name.

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:5198/api/v1/chat/direct `
  -ContentType "application/json" `
  -Body '{"message":"Explain what this starter kit is for.","correlationId":"demo-direct-chat-1"}'
```

Upload a document for indexing:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:5198/api/v1/documents `
  -Form @{
    title = "Demo Notes"
    accessLevel = "TenantPublic"
    file = Get-Item .\samples\documents\demo-notes.md
  }
```

Check indexing status:

```powershell
Invoke-RestMethod `
  -Method Get `
  -Uri http://localhost:5198/api/v1/documents/<document-id>
```

Inspect local AI usage:

```powershell
Invoke-RestMethod `
  -Method Get `
  -Headers @{ "X-Demo-User-Id" = "alice"; "X-Demo-Tenant-Id" = "local" } `
  -Uri "http://localhost:5198/api/v1/usage?from=2026-05-01T00:00:00Z&to=2026-05-31T23:59:59Z&tenantId=local&model=mock-chat"
```

Run sample evaluations:

```powershell
$run = Invoke-RestMethod `
  -Method Post `
  -Headers @{ "X-Demo-User-Id" = "alice"; "X-Demo-Tenant-Id" = "local" } `
  -Uri http://localhost:5198/api/v1/evaluations/runs `
  -ContentType "application/json" `
  -Body '{"datasetVersion":"sample-v1","correlationId":"demo-eval-1"}'

Invoke-RestMethod `
  -Method Get `
  -Headers @{ "X-Demo-User-Id" = "alice"; "X-Demo-Tenant-Id" = "local" } `
  -Uri "http://localhost:5198/api/v1/evaluations/runs/$($run.runId)/summary"
```

The evaluation API is synchronous in this starter kit:
`POST /api/v1/evaluations/runs` returns `200 OK` with the completed run after
all cases finish. The summary endpoint reads the persisted result for that run.

Run the evaluation CLI with the shared Application evaluation service:

```powershell
dotnet run --project src/GenAIPlatform.Evaluations -- run
```

Run bounded agentic chat with a safe demo tool:

```powershell
Invoke-RestMethod `
  -Method Post `
  -Headers @{ "X-Demo-User-Id" = "alice"; "X-Demo-Tenant-Id" = "local" } `
  -Uri http://localhost:5198/api/v1/chat/agentic `
  -ContentType "application/json" `
  -Body '{"message":"Use my profile.","correlationId":"demo-agentic-1"}'
```

## Provider Overrides

To use an OpenAI-compatible chat completions endpoint, override configuration
locally:

```powershell
$env:GenAIPlatform__ModelGateway__Provider = "OpenAiCompatible"
$env:GenAIPlatform__ModelGateway__DefaultModel = "<model-name>"
$env:GenAIPlatform__ModelGateway__OpenAiCompatible__ApiKey = "<api-key>"
```

Chat completion retries honor positive `Retry-After` seconds or future dates,
with exponential fallback for unusable hints and 0 through 20 percent jitter.
The model-only `GenAIPlatform__ModelGateway__OpenAiCompatible__RetryMaxDelaySeconds`
setting defaults to `30` and accepts `1` through `300`. The final delay saturates
at this cap, so a 60-second hint waits 30 seconds with defaults. HTTP 501 and 505
are not retried; 408, 429 and other 5xx are. Caller cancellation interrupts
backoff and prevents another attempt. See [model retries](model-gateway.md#chat-completion-retries).

To use an OpenAI-compatible embeddings endpoint, override configuration locally:

```powershell
$env:GenAIPlatform__Embeddings__Provider = "OpenAiCompatible"
$env:GenAIPlatform__Embeddings__DefaultModel = "<embedding-model-name>"
$env:GenAIPlatform__Embeddings__OpenAiCompatible__ApiKey = "<api-key>"
```

For RAG with real embeddings, set the embedding overrides in both the API
terminal and the Worker terminal. The API creates the query embedding used for
retrieval, while the Worker creates and stores document chunk embeddings during
indexing. If those processes use different providers, models or dimensions,
retrieval correctly treats the vectors as incompatible and can return no
context.

Chat completion overrides are needed in the API terminal for direct, RAG and
agentic chat requests. Set the same model gateway overrides in the Evaluations
CLI environment if you run evaluations through the CLI.

OpenAI-compatible provider URLs must use HTTPS by default. For a local loopback
test server only, set
`GenAIPlatform__ModelGateway__OpenAiCompatible__AllowInsecureHttpForLoopback=true`.

The public sample path uses mock providers. If you run the demo with real
OpenAI-compatible credentials, sanitize any `/api/v1/usage` output before
sharing it; do not fabricate real-provider evidence from mock-provider data.

## Demo Flow Checklist

1. `docker compose up -d postgres`
2. Set `ConnectionStrings__GenAIPlatform` and the same absolute `GenAIPlatform__DocumentStorage__RootPath` in the API terminal.
3. `dotnet run --project src/GenAIPlatform.Api --no-build --launch-profile http`
4. Set `ConnectionStrings__GenAIPlatform` and the same absolute `GenAIPlatform__DocumentStorage__RootPath` in the Worker terminal.
5. `dotnet run --project src/GenAIPlatform.Worker --no-build`
6. `GET /api/v1/health`
7. `POST /api/v1/chat/direct`
8. Upload [samples/documents/demo-notes.md](../samples/documents/demo-notes.md)
9. Poll `GET /api/v1/documents/{documentId}` until `Indexed`
10. `POST /api/v1/chat/rag`
11. `GET /api/v1/usage`
12. `POST /api/v1/evaluations/runs` and fetch the summary
13. `dotnet run --project src/GenAIPlatform.Evaluations -- run`
14. `POST /api/v1/chat/agentic`
