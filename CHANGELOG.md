# Changelog

## Unreleased

No unreleased changes.

## v0.4.0 - 2026-09-11

This reference release adds verified PostgreSQL database migrations, a labeled
retrieval-quality baseline and untrusted-content framing for retrieved RAG
context, without changing its non-production framing.

- Retrieved chunks are rendered inside backend-owned `<source id title file>`
  frames with attribute escaping and neutralization of `<source`/`</source>`
  markers found in document text; budget accounting includes framing overhead,
  truncation never splits a surrogate pair, and Evaluations reuse the same
  builder. Framing is not a security boundary: a model can still write a
  bracketed citation id that has no frame, and the response `citations` array
  lists only real frames.
- The local stdio MCP host logs one sanitized startup warning when its
  configured identity carries the `admin` role, which grants cross-tenant
  usage reads; blank user or tenant identity fails startup before any hosted
  service runs. Documentation states the host trusts its configuration file as
  the caller identity and that per-caller remote authentication remains future
  scope.
- Added a labeled retrieval baseline: a lexical mock embedding variant
  (`GenAIPlatform:Embeddings:MockVariant=Lexical`, provider `mock-lexical`; the
  SHA-256 hash mock stays the default), an embedded dataset of 19 documents, 21
  chunks and 34 labeled queries across relevant, distractor, paraphrase,
  no-match, tenant-isolation, private-ownership, document-version and
  embedding-compatibility categories, Domain-level Recall@K, first relevant
  rank, reciprocal rank and no-match accuracy metrics, and a
  `retrieval-baseline` CLI verb that runs in isolated `retrieval-baseline*`
  tenants, writes a report of ids, hashes, settings and metrics only, and
  exits nonzero on gate failure without making model-completion calls.
- Added verified PostgreSQL database migrations: an Infrastructure-owned
  runner with a `genai.schema_migrations` journal (SHA-256 checksums and an
  adoption flag), `genai.schema_migration_attempts`, a session advisory lock,
  embedded migrations `0001`-`0006` (byte-identical to the former init scripts
  `002`-`007`), frozen v0.3.1 fingerprint adoption that only ever matches
  exactly and fails closed on a partial or unknown schema, and the one-shot
  `GenAIPlatform.Migrations` host (`migrate`, `status`; exit codes `0`/`1`/`2`).
  Retrieval and indexing readiness are coupled to the journal head, and
  `infra/postgres/init/` now keeps only `001-enable-pgvector.sql`.
- Migration `0007-single-embedding-column` removes the duplicate
  `embedding_values` relational array after re-running the backfill, failing
  closed (reporting counts only) on any chunk without a vector or whose vector
  disagrees with its array; `embedding_vector` becomes `NOT NULL` and the chunk
  writer stops writing the array. A measured experiment (40,000 synthetic
  chunks, PostgreSQL 16.13, pgvector 0.8.2) found that the production search
  query never reaches the partial HNSW indexes, so retrieval is exact pgvector
  search for every dimension today; no query was changed.
- Documentation corrections: the retrieval-baseline frozen report's recorded
  revision sentence, the removed `infra/postgres/init/002..007` script paths in
  the quickstart and versioning upgrade notes, the RAG-safety-review skill's
  in-scope schema path, and a stale "v0.2.0 host" label in the MCP
  documentation.

### Upgrade Requirement

Run the migration host (`dotnet run --project src/GenAIPlatform.Migrations --
migrate`) against the target database before starting the API, Worker or MCP
host. `v0.3.1` is the supported source version for adoption; migration `0007`
has a precondition that every chunk already has an `embedding_vector` (see
`docs/quickstart.md` for the diagnostic query and repair steps). The former
`infra/postgres/init/002..007` scripts are removed; only
`001-enable-pgvector.sql` remains, and all other schema objects now come from
the embedded migrations.

## v0.3.1 - 2026-09-08

This correctness, safety, build and repository-hygiene patch updates the
reference starter kit without changing its non-production framing.

- Corrected public tool, approval and schema-validation claims; added a
  deterministic RAG-demo screenshot and concise consumer-facing agent guidance.
- Added release CI checks with immutable action pins and least-privilege jobs.
- Added build-enforced style, centralized package versions, warnings-as-errors,
  xUnit 4 through Microsoft Testing Platform v2, and a guarded Dependabot
  whole-graph lock-file synchronization workflow.
- Enforced bounded declared JSON schemas for governed tools, including frozen
  external snapshots and explicit schemaless exceptions.
- Made external MCP lifecycle and audit behavior safer: bounded result content,
  metadata-only external audit records, cancelable shutdown, no automatic replay
  after an uncertain dispatched effect, and explicit run termination.
- Stopped agentic execution when token usage cannot be accounted for; added
  bounded safe diagnostics; authenticated before usage authorization and return
  403 for non-admin cross-tenant scope.
- Honored bounded jittered `Retry-After` model backoff, aligned Worker settings
  with the API, expanded the code-organization gate, simplified composition and
  status wrappers, and tightened API error mapping.
- Extracted test fixtures, removed the attribute meta-test, and split test files
  to meet the test-file line limit.
- Tightened agentic partial-usage consistency, content-free argument-limit audit
  errors, and JSON-schema location and reference-target checks.
- Made the two-host mock demo reproducible with shared clone-derived storage and
  prebuilt host commands; refreshed its neutral recorded evidence image and the
  local MCP host architecture diagrams.

## v0.3.0

Adds MCP client support for consuming external stdio MCP servers as Agentic
tools under first-party governance. External tools are adapted in
Infrastructure behind an Application Agentic port, then routed through the
same validation, policy, approval, budget and audit path as built-in tools.

The external MCP adapter captures tool definitions at connect time, writes the
snapshot hash as backend-owned schema provenance, sanitizes tool descriptions,
prefixes provider-safe names as `mcp_<server>_<tool>` and makes every external tool
approval-required by default. Server configuration is explicit under
`GenAIPlatform:ExternalMcp:Servers`, with enabled servers and optional
per-server `AllowedTools` limiting what can appear in the agentic registry.

Connection handling is resilient: startup is non-blocking, servers connect
bounded-parallel without changing the deterministic tool listing, a server that
is unavailable at startup recovers on a background refresh pass, and a per-server
connection policy seam gates connect attempts. `ConnectOnStartup`,
`MaxParallelConnects` and `RefreshInterval` are configurable; circuit-breaker
backoff escalation remains future work behind the policy seam.

The v0.3.0 test coverage includes the composite Agentic registry,
Infrastructure adapter behavior, deterministic external-tool listing,
JSON argument fidelity, timeout/cancellation handling, timeout-to-unavailable behavior, unavailable-server
degradation and governance/audit integration including approval-required,
blacklist-before-wrapper-policy and snapshot/rug-pull scenarios. The automated gate does not depend
on real child-process MCP servers.

Documentation now covers external MCP configuration and governance guarantees.
This remains a starter kit: production remote authentication, secret storage
and enterprise connector management are future adapter work.

Not a production system.

## v0.2.0

Adds a local stdio MCP host as a fourth consumption surface over the existing
Application modules. The host exposes bounded read-only tools for server info,
permission-aware RAG answers, usage totals and the governed current-user profile
tool.

The governed MCP tool calls the Agentic direct tool-execution use case, so safe
tool execution still goes through backend policy and writes `tool_audit_logs`.
Approval-required tools are not exposed as successful MCP host actions; direct
use-case calls without approval fail closed with `approval_required` and are
audited.

Documentation now covers the MCP host, Claude Desktop configuration, safe-only
host limitation and the internal dispatcher swap path if a team chooses MediatR
in its own application.

Repository maintenance is also documented: changes land on protected `main`
through pull requests, public release PRs update `VERSION` and release notes,
and the `publish-release` workflow runs automatically after that `VERSION`
change reaches `main`. It tags the current `main` HEAD and publishes the GitHub
release from the matching `docs/release-notes` file. See `docs/versioning.md`
and `AGENTS.md`.

Not a production system.

## v0.1.0

Initial public reference release.

A .NET 10 starter kit for the backend layer around GenAI applications: RAG over
PostgreSQL + pgvector, model gateway abstraction, prompt versioning, permission-aware
retrieval, sanitized AI request logging, usage/cost tracking, API and CLI evaluations,
and bounded agentic chat with backend-controlled tools.

Modular Clean Architecture: `Domain`, Application modules
(`Core` / `Knowledge` / `Generation` / `Agentic` / `Evaluations` / `Usage`),
`Infrastructure`, and host projects (`Api` / `Worker` / `Evaluations` CLI).
Deterministic mock providers by default; OpenAI-compatible adapters behind ports.

Not a production system.
