# v0.3.1 Reference Release Notes

`v0.3.1` is a correctness, safety, build and repository-hygiene patch over
`v0.3.0`. It remains a reference starter kit, not a production deployment or
stable framework.

Release date: 2026-09-08.

## Changed

- Documentation now states the implemented limits of tool denylisting,
  request-scoped simulated approval and schema validation, and the README adds
  a deterministic mock-provider RAG example.
- Builds enforce the repository style policy, central package versions and
  warnings-as-errors. Tests run through xUnit 4 and Microsoft Testing Platform
  v2 with explicit solution or project commands.
- Release verification uses immutable action pins, least-privilege jobs and the
  full Docker-backed suite. A guarded workflow can prepare a complete
  Dependabot lock-file repair, subject to its repository and credential guards.
- Governed tools validate bounded declared Draft 2020-12 schemas before tool
  semantics. External tool schemas are captured in a frozen snapshot; only an
  explicit per-server schemaless exception is allowed.
- External MCP execution has one lifecycle owner, bounded provider-neutral
  output and metadata-only durable audit records. An uncertain dispatched call
  stops the current agentic run instead of being replayed automatically.
- Agentic runs stop before another tool or model step when usage cannot be
  accounted for. Usage-scope authorization authenticates before role checks and
  returns 403 for non-admin cross-tenant or cross-user scope.
- Model retries honor bounded jittered `Retry-After` hints. Worker ingestion,
  storage and PostgreSQL configuration is explicitly aligned with the API.
- The code-organization gate scans production and test sources, composition and
  status wrappers were reduced, multipart 413 handling is typed, and test
  fixtures were extracted under the test-file line limit.

## Removed

- The unbound `FullPromptLoggingEnabled` configuration sample, uncollected
  Coverlet collector, per-project package-version duplication, unused using
  directives and self-namespace imports are removed.
- Automatic replay of an external MCP call after an uncertain dispatched effect
  is removed.
- Internal refactors remove the broad ingestion repository facade, the
  pass-through document-upload workflow wrapper, redundant status-name wrappers
  and the empty Domain `Setup` type. These are not public API removals.
- The direct tag-push release trigger, `TestIsolationTests` attribute meta-test
  and temporary oversized-test allowances are removed.

## Still Not Done

- Production authentication and rate limiting are not part of this patch.
- Approval remains a request-scoped simulation, not an independent
  second-principal approval.
- Tokenizer-based budgets, streaming and RAG chunk-delimiter hardening remain
  future work.
- MCP host identity hardening, exactly-once external effects, remote idempotency
  and automatic reconciliation are not provided. Operators must reconcile
  unknown external outcomes through manual operator reconciliation.
- Database migration runner, pgvector query and index optimization, and batched chunk inserts remain deferred.
- The local gate does not execute real child-process MCP servers or live model
  and embedding providers. Hosted Dependabot auto-push execution is unproven.

## Verification Scope

The final local release gate passed on 2026-09-08: locked restore, build with
zero warnings and errors, formatting verification, the code-organization gate
(564 authored C# files: 481 production and 83 test), and the package
vulnerability gate. The mandatory Docker-backed MTP solution suite passed 672
tests with zero failures and zero skipped tests.

Tests use mock and loopback providers only; no live-provider or real
child-process MCP execution is claimed.
