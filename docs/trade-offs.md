# Trade-offs

This document records intentional choices and their costs.

## pgvector vs Azure AI Search

pgvector is simple, local and cost-effective. Azure AI Search is more enterprise-ready and feature-rich. The starter kit begins with pgvector and allows an Azure AI Search adapter later.

## pgvector Dimension-Specific Indexes

The retrieval schema stores embeddings in a dimensionless pgvector column so mock and real embedding providers can coexist during the starter-kit phase. HNSW indexes require fixed dimensions, so the migrations create partial indexes for the default 16-dimension mock embeddings and common 1536-dimension embeddings, but the production search query does not reach them: the deterministic tie-breakers, the tenant/permission join and the similarity-threshold predicate all keep the planner on a bitmap scan and heapsort instead, as measured in `docs/rag-pipeline.md#measurements`. Retrieval is exact pgvector search for every dimension today. Making the indexes reachable would mean giving up the deterministic tie-breakers or the exact top-K guarantee, which is a separate decision with its own evaluation, not something this starter kit has done.

Retrieval still filters by embedding provider and model before ranking. Dimension-only compatibility is not enough because different embedding models can produce vectors in incompatible spaces even when their dimensions match.

## One Stored Embedding Representation

Until v0.3.1 a chunk stored its embedding twice: a relational `real[]` column kept for auditability, and the pgvector column search reads. The array was never read by anything except the 0002 backfill, so the second copy bought inspectability that `embedding_vector` already provides while doubling the write, doubling the storage, and creating a state nothing could detect: two columns for the same chunk that no longer agree. Migration `0007-single-embedding-column` dropped the array.

The cost is paid at upgrade time rather than at run time. 0007 refuses to run on a database that still holds a chunk with no vector, or with a vector that disagrees with its array, because dropping the array from such a row would destroy the only embedding it has. That turns a class of legacy row that v0.3.1 tolerated silently into an upgrade that stops and asks for a repair. The alternative considered was dropping the array unconditionally and letting affected chunks disappear from retrieval without a signal; that trades a visible, actionable failure for silent data loss, which is the wrong way round.

Nothing about search semantics changed with it. `embedding_vector` was already the only column the query reads, so the retrieval baseline in `docs/evaluations/retrieval-baseline-v1.json` reproduces every quality field unchanged across the migration.

## Hand-Rolled Migration Runner vs A Migration Library

The schema migration runner is hand-rolled on raw Npgsql rather than built on a migration library. Two candidates were checked on NuGet on 2026-09-10. `Evolve` 3.2.0 is the latest stable release and was published 2023-06-30, with nothing since. `dbup-postgresql` 7.0.1 (2026-02-23) is maintained, but it does not re-validate the checksums of already applied scripts, has no PostgreSQL advisory lock, and pulls its own Npgsql beside the pinned 10.0.3 in `Directory.Packages.props`.

This item needs checksum re-validation of applied scripts, a bounded advisory lock, a strict legacy-schema fingerprint before adoption, and failure records written outside the aborted transaction. Those behaviors would be custom code with either library, so the library would add a dependency and a second Npgsql without removing the work. A raw-Npgsql runner also matches the existing lease and job code style, which is the code a reader of this starter kit is already looking at.

The cost is real: no community-maintained runner, no baseline/repair tooling, and no down scripts. Downgrade is restore from backup. If the persistence surface grows beyond a starter kit, or if EF Core is adopted for broader persistence, revisit this and prefer that ecosystem's migrations.

## Simple Prompt Templates vs Full Prompt Management

Simple templates are enough for the starter-kit scope. Full prompt management may need UI, approvals, diffs, evaluation gates and rollout strategy.

## Mock Model vs Real Model In Tests

Real model calls are expensive and nondeterministic. Automated tests use mock clients by default.

## Full Prompt Logging vs Privacy

Full prompt logs help debugging but may leak sensitive data. Default logging is metadata-only.

## Simple Access Control vs Enterprise RBAC

The current implementation uses private and tenant-public document access metadata only. Shared user/group grants are deferred until durable grant metadata and retrieval filters are implemented; the architecture should not block later RBAC or Entra ID integration.

## Domain Records with Application-Owned Behavior

Domain types are intentionally simple records and enums in the starter-kit scope, but domain concepts live in the Domain layer so they can be reused across Application workflows without creating Application-to-Application coupling. Workflow behavior, validation policy and partial-failure handling stay in Application services so the public sample remains easy to inspect without DDD ceremony.

## Simple Evals vs LLM-as-Judge

Rule-based evals are deterministic and easy. LLM-as-judge can be useful later, but adds cost and nondeterminism.

## Starter Kit vs Framework

A starter kit is easier to build and understand. A framework requires stable APIs, compatibility guarantees and long-term support.

## Internal Dispatcher vs MediatR

A lightweight internal dispatcher keeps the starter kit dependency-light. MediatR v12 can be familiar for many .NET developers, but newer MediatR versions may introduce licensing considerations. This project uses an internal dispatcher/pipeline and can document MediatR as an optional alternative later.

The replacement boundary is the dispatcher engine, not the hosts or use-case contracts. A MediatR swap should replace `ApplicationDispatcher`, `RequestValidationBehavior`, `DispatchLoggingBehavior` and the dispatcher delegate shape with MediatR request handling and pipeline behaviors. The stable contracts are `IApplicationDispatcher`, the request marker interfaces and module-owned `IRequestHandler<TRequest, TResponse>` handlers. Hosts should continue depending on `IApplicationDispatcher` and explicit per-module registration methods so API, Worker, Evaluations and MCP composition do not learn which dispatcher engine is active.

That swap would still require deliberate adapter work because the current dispatcher signatures are not MediatR signatures. Keeping the seam at `IApplicationDispatcher` avoids spreading a framework dependency across hosts while preserving a clear migration path if a team prefers MediatR in its own application.

## MCP Host Safe Tool Surface

The v0.2.0 MCP host intentionally exposes only safe, bounded tools over existing Application use cases. It does not expose a generic registry executor, and approval-required tools are outside the MCP host surface because there is not yet a broadly supported interactive approval flow across MCP clients. A direct governed-tool use-case call for a `RequiresApproval = true` tool fails closed with `approval_required` and still writes audit; the host does not provide a successful execution path for that class of tool until protocol and client support make approvals explicit.

## FluentValidation vs Custom Validators

FluentValidation is used for request-shape validation because it is familiar to many .NET teams, has no MediatR dependency and keeps rule composition separate from handler orchestration. Handlers that need normalized value objects use a neighboring `Normalizer.cs` instead of asking validators to both reject invalid input and build workflow state.

The MediatR decision remains separate. This project still uses its internal dispatcher and pipeline behaviors; FluentValidation provides the rule engine only.

## .NET 10 LTS vs Older Targets

.NET 10 LTS is the preferred baseline for a new project started in 2026. Older .NET versions may be familiar to more teams, but they have shorter remaining support windows.

## `v0.1.0` vs `v1.0.0`

`v0.1.0` communicates that the project is useful but evolving. `v1.0.0` should wait until contracts, docs and extension points are stable.

## Raw String Identifiers vs Strongly-Typed Value Objects

Identifiers like `TenantId`, `UserId`, `DocumentId` are passed as `string` and `Guid` throughout the codebase rather than as strongly-typed value objects (e.g. `readonly record struct TenantId`). Value objects offer compile-time safety against argument-mix-ups and centralized validation, but introduce friction with `System.Text.Json`, `Npgsql` parameter binding, and `IOptions<T>` binding at the starter-kit scope. The current implementation accepts the small risk of string mix-ups in exchange for transport simplicity. A future scope that grows multi-context handler signatures (tenant + user + correlation + ...) may revisit this.
