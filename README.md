# .NET-native GenAI Platform Starter Kit

A .NET 10 reference implementation for a practical, secure backend platform layer around GenAI applications.

1. Upload [`samples/documents/demo-notes.md`](samples/documents/demo-notes.md) as a tenant-public document.
2. The Worker indexes it with the deterministic local mock providers.
3. Ask a RAG question and receive an answer with citation `[1]`.
4. Read the usage line for request count, tokens and estimated cost.

![Deterministic local RAG demo result with citation and usage summary](docs/images/genai-platform-rag-demo.png)

## Architecture

```mermaid
flowchart LR
    Client["Client / API consumer"] --> Api["GenAIPlatform.Api"]
    Api --> Modules["Application modules"]
    Worker["GenAIPlatform.Worker"] --> Knowledge["Core + Knowledge"]
    Evaluations["GenAIPlatform.Evaluations CLI"] --> EvalModules["Core + Knowledge + Generation + Evaluations"]
    Mcp["GenAIPlatform.Mcp (local stdio host)"] --> McpModules["Core + Knowledge + Generation + Agentic + Usage"]
    Migrations["GenAIPlatform.Migrations (one-shot host)"] --> Infrastructure
    Modules --> Domain["GenAIPlatform.Domain"]
    Knowledge --> Domain
    EvalModules --> Domain
    McpModules --> Domain
    Infrastructure["GenAIPlatform.Infrastructure"] --> Modules
    Infrastructure --> Postgres["PostgreSQL + pgvector"]
    Infrastructure --> Storage["Local document storage"]
    Infrastructure --> Providers["Mock or OpenAI-compatible providers"]
```

The project uses Clean Architecture with CQRS-lite where it improves clarity. API endpoints stay thin, Application modules own use cases and ports, Domain stays provider-agnostic, Infrastructure implements persistence/provider adapters, and Worker runs background jobs through the Core and Knowledge modules.

`GenAIPlatform.Mcp` adds a local stdio MCP host as the fourth consumption surface: REST for HTTP callers, Worker for background jobs, the Evaluations CLI for offline runs and MCP for AI clients. It composes Core, Knowledge, Generation, Agentic and Usage, and exposes a bounded read-only tool set over existing Application use cases, including one governed safe Agentic tool that still goes through backend policy and audit.

External MCP servers are consumed separately by Infrastructure as Agentic tool sources. Their tools are not exposed through the local MCP host as a generic executor; they appear in the platform's agentic loop only after allow-listing, snapshotting, name prefixing and backend approval policy.

## What This Is Not

- Not a production-ready framework or a claim of production scale, live traffic or enterprise deployment.
- Not a no-code builder, SaaS platform or ML training platform; demo auth, mock providers and demo tools are intentionally replaceable adapters.

## Current Status

The `v0.4.0` reference release adds verified PostgreSQL database migrations, a labeled retrieval-quality baseline and untrusted-content framing of retrieved text in RAG prompts, over `v0.3.1`; it retains the local MCP host and external MCP client support. See the [v0.4.0 release notes](docs/release-notes-v0.4.0.md) for the release detail and the documentation below for architecture and safety boundaries.

The public sample path uses deterministic mock providers. OpenAI-compatible adapters are covered by loopback integration tests, while Docker-backed tests cover local persistence behavior; this repository does not publish live-provider results because they depend on private credentials and account-specific behavior.

## How This Was Built

Implementation and refactoring were heavily assisted by coding agents. Igor Lagutin owned the
product scope, architecture, task decomposition, acceptance criteria, review decisions and release
gates. Generated changes were treated as untrusted until the relevant build, tests, Docker scenarios,
code-organization checks and vulnerability gate passed.

Failures and trade-offs stay visible in the repository. Its published verification covers mock-provider, loopback and Docker-backed checks; live-provider results are not published.

Start here:

- [Architecture](docs/architecture.md)
- [Application pipeline](docs/application-pipeline.md)
- [Versioning](docs/versioning.md)
- [Trade-offs](docs/trade-offs.md)

## Implemented Scope

- RAG with PostgreSQL + pgvector.
- Model gateway abstraction.
- Prompt template versioning.
- Permission-aware retrieval.
- Sanitized AI request logs, usage/cost tracking and documented observability extension points.
- Evaluation runner through API and CLI.
- Safe tool execution and bounded agentic chat.
- Local MCP host as a fourth consumption surface alongside REST, Worker and CLI.
- Configured external MCP tools routed through the same Agentic validation, policy, request-scoped simulated approval and audit path as built-in tools.
- Verified database migrations with a one-shot migration host.
- Retrieval quality baseline with Recall@K, first relevant rank and no-match accuracy on a labeled synthetic dataset.
- Untrusted-content framing of retrieved text in RAG prompts.
- Docker Compose local development.
- Clean/modular .NET architecture.

## Documentation

- [Quickstart](docs/quickstart.md)
- [RAG pipeline](docs/rag-pipeline.md)
- [Security model](docs/security-model.md)
- [Model gateway](docs/model-gateway.md)
- [Prompt versioning](docs/prompt-versioning.md)
- [Evaluations](docs/evaluations.md)
- [Cost tracking](docs/cost-tracking.md)
- [Safe tools](docs/safe-tools.md)
- [Agentic chat](docs/agentic-chat.md)
- [MCP host](docs/mcp.md)
- [Observability](docs/observability.md)
- [Local demo walkthrough](docs/local-demo.md)
- [v0.4.0 release notes](docs/release-notes-v0.4.0.md)
- [v0.3.1 release notes](docs/release-notes-v0.3.1.md)
- [v0.3.0 release notes](docs/release-notes-v0.3.0.md)
- [v0.2.0 release notes](docs/release-notes-v0.2.0.md)
- [v0.1.0 release notes](docs/release-notes-v0.1.0.md)

## Target Stack

- .NET 10 LTS.
- ASP.NET Core.
- PostgreSQL.
- pgvector.
- Docker Compose.
- OpenAI-compatible model and embedding clients.
- Model gateway with documented [chat completion retry behavior](docs/model-gateway.md#chat-completion-retries).
- Mock model and embedding clients for tests.
- Sanitized AI request logs, usage/cost tracking and documented observability extension points.
- xUnit.
- Testcontainers for integration tests.

## Project Structure

```text
src/
  GenAIPlatform.Api
  GenAIPlatform.Application.Core
  GenAIPlatform.Application.Knowledge
  GenAIPlatform.Application.Generation
  GenAIPlatform.Application.Agentic
  GenAIPlatform.Application.Evaluations
  GenAIPlatform.Application.Usage
  GenAIPlatform.Domain
  GenAIPlatform.Infrastructure
  GenAIPlatform.Mcp
  GenAIPlatform.Migrations
  GenAIPlatform.Worker
  GenAIPlatform.Evaluations
tests/
  GenAIPlatform.UnitTests
  GenAIPlatform.IntegrationTests
```

`GenAIPlatform.Migrations` is the one-shot host that applies the packaged PostgreSQL schema migrations; no other host migrates on startup.

## Quickstart

See [docs/quickstart.md](docs/quickstart.md) for the full local setup, sample requests, demo identity headers and provider override examples.

Minimal local path:

```powershell
Copy-Item .env.example .env
dotnet restore GenAIPlatform.slnx
dotnet build GenAIPlatform.slnx
dotnet test --solution GenAIPlatform.slnx
docker compose up -d postgres
$env:ConnectionStrings__GenAIPlatform = "Host=localhost;Port=5432;Database=genai_platform;Username=genai;Password=genai_dev_password"
dotnet run --project src/GenAIPlatform.Migrations -- migrate
```

Run the API and Worker in separate terminals. In each terminal, start from the
root of your clone. The setup lines below intentionally match: they set one
shared absolute storage location, the same local database, and explicit mock
providers. PowerShell environment variables are process-local, so repeat them
for both hosts.

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

Sample HTTP requests are available in [src/GenAIPlatform.Api/GenAIPlatform.Api.http](src/GenAIPlatform.Api/GenAIPlatform.Api.http) and [samples/http/local-demo.http](samples/http/local-demo.http). The local demo file covers direct chat, document upload, RAG, usage, evaluations and agentic chat.
