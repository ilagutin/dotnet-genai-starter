# Changelog

## Unreleased

No unreleased changes.

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
