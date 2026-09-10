# v0.4.0 Reference Release Notes

`v0.4.0` adds verified PostgreSQL database migrations, a labeled
retrieval-quality baseline and untrusted-content framing for retrieved RAG
context, over `v0.3.1`. It remains a reference starter kit, not a production
deployment or stable framework.

Release date: 2026-09-11.

## Changed

- Retrieved chunks are rendered inside backend-owned `<source id title file>`
  frames with attribute escaping and neutralization of `<source`/`</source>`
  markers found in document text. Budget accounting includes framing overhead,
  truncation never splits a surrogate pair, Evaluations reuse the same prompt
  builder, and the `rag-chat` v1 prompt template is unchanged. Framing is not a
  security boundary: a model can still write a bracketed citation id that has
  no frame, and the response `citations` array lists only real frames.
- The local stdio MCP host logs one sanitized warning at startup when the
  configured identity carries the `admin` role, which grants cross-tenant
  usage reads. Blank user or tenant identity now fails startup before any
  hosted service runs. Documentation states that the host trusts its
  configuration file as the caller identity and that per-caller remote
  authentication is future scope.
- Added a labeled retrieval baseline as a second, narrower evaluation. A
  lexical mock embedding variant
  (`GenAIPlatform:Embeddings:MockVariant=Lexical`, provider `mock-lexical`)
  joins the existing SHA-256 hash mock, which stays the default. An embedded
  dataset (`retrieval-baseline.v1.json`) holds 19 documents, 21 chunks and 34
  labeled queries across relevant, distractor, paraphrase, no-match,
  tenant-isolation, private-ownership, document-version and
  embedding-compatibility categories. Domain-level metrics cover Recall@K over
  documents in the top-K chunks, first relevant rank, reciprocal rank and
  no-match accuracy. The `retrieval-baseline` CLI verb runs against isolated
  `retrieval-baseline*` tenants, writes a report containing ids, hashes,
  settings and metrics only, exits nonzero on a gate failure, and makes no
  model-completion calls. The frozen reference report,
  `docs/evaluations/retrieval-baseline-v1.json`, records recall@3 of 1.0 over
  21 eligible queries, an MRR of 1.0, and a no-match accuracy of 1.0 over 13
  queries, against gates of k=3, recall 0.9 and no-match accuracy 1.0.
  Mock-vector results are engineering checks, not semantic-quality evidence.
- Added verified PostgreSQL database migrations. An Infrastructure-owned
  runner tracks a `genai.schema_migrations` journal (version, name, SHA-256
  checksum of the canonicalized SQL, applied timestamp, applying role,
  duration and an adoption flag) and a `genai.schema_migration_attempts` table
  for failed or uncertain attempts, serialized on a session-scoped PostgreSQL
  advisory lock. Migrations `0001`-`0006` are embedded in the Infrastructure
  assembly and are byte-identical to the former init scripts `002`-`007`. A
  frozen v0.3.1 schema fingerprint is adopted only on an exact match; a
  partial or otherwise unrecognized schema fails closed with a sanitized
  message and changes nothing. The one-shot `GenAIPlatform.Migrations` host
  exposes `migrate` and `status` verbs with exit codes `0` (up to date or
  applied), `1` (behind, or the migration failed) and `2` (usage or
  configuration error). RAG retrieval and indexing job processing now fail
  their readiness checks with a message naming the migration command while the
  journal is missing or behind; no host migrates on startup.
  `infra/postgres/init/` keeps only `001-enable-pgvector.sql`, so Docker
  initialization and the upgrade path never diverge on schema content.
  Hand-rolled rather than adopting an existing tool, because Evolve's last
  release (3.2.0) dates from 2023-06-30 and dbup-postgresql lacks checksum
  re-validation and an advisory lock.
- Migration `0007-single-embedding-column` removes the duplicate
  `embedding_values` relational array column after re-running the backfill. It
  fails closed, reporting only counts of affected chunks and documents, on any
  row without a vector or whose vector disagrees with its array;
  `embedding_vector` becomes `NOT NULL` and the chunk writer stops writing the
  array. A measured experiment (40,000 synthetic chunks, PostgreSQL 16.13,
  pgvector 0.8.2, documented in `docs/rag-pipeline.md`) found that the
  production search query never reaches the partial HNSW indexes, so retrieval
  is exact pgvector search for every dimension today; no query was changed by
  this release.
- Documentation corrections: the retrieval-baseline frozen report's recorded
  revision sentence in `docs/evaluations.md` now names the current revision;
  `docs/quickstart.md` and `docs/versioning.md` no longer point at the removed
  `infra/postgres/init/002..007` script paths; the `rag-safety-review` skill's
  in-scope file list points at the current migrations SQL location; and the
  MCP documentation no longer carries a stale "v0.2.0 host" label.

## Removed

- The former `infra/postgres/init/002-007` initialization scripts. Their SQL
  moved, byte-identical, into embedded migrations `0001`-`0006`; only
  `001-enable-pgvector.sql` remains under `infra/postgres/init/`.
- The relational `embedding_values real[]` chunk column, dropped by migration
  `0007-single-embedding-column`. A chunk's embedding now has one stored
  representation, the pgvector `embedding_vector` column.

## Still Not Done

- The RAG question text itself is not neutralized against a forged source
  frame; only retrieved document context is framed. A frame-aware prompt
  template and dangling-citation validation (rejecting a model citation id
  that has no matching frame) remain future work.
- The MCP identity validator does not reject a control-only or otherwise
  degenerate configured identifier beyond the existing blank-value check.
- Additional migration test hardening (broader fault-injection coverage of
  interrupted commits and concurrent adoption races) remains a backlog item.
- Whether pgvector HNSW indexes should be made reachable by the production
  search query is an open decision; today they are not used, and search is
  exact for every dimension.
- Hybrid retrieval, streaming responses and per-caller remote MCP
  authentication remain future work.
- This repository does not publish live-provider results; mock-provider,
  loopback and Docker-backed results are published, but real embedding or
  chat provider behavior depends on private credentials and account-specific
  behavior. Mock-vector retrieval-baseline numbers are engineering checks on
  filtering, ranking and de-duplication, not evidence of semantic answer
  quality.

## Verification Scope

Each change was reviewed against its written acceptance criteria independently
of its author before it was committed. The final local release gate on the
assembled branch at `d48bd22` passed: locked restore, a Release build with zero
warnings, formatting verification, the code-organization gate (685 files) and
the package vulnerability gate. The mandatory Docker-backed MTP solution suite
passed 912 tests: 912 total, 0 failed, 0 skipped, against Docker 29.4.0,
Testcontainers `pgvector/pgvector:pg16`, PostgreSQL 16.13 and pgvector 0.8.2.

Independent end-to-end checks ran on throwaway databases: a fresh database
migrated to `0007` together with an API RAG flow producing a live frame; a
frozen v0.3.1 database with seeded rows adopted and upgraded with data
preserved, with readiness failing before migration and passing after; and the
retrieval baseline reproduced field-for-field. A manual model comparison for
the framing change (LM Studio, model `qwen3.8-27b-uncensored`, temperature 0,
seed 42, five synthetic cases before and after the format change) found normal
citation behavior and instruction-injection resistance unchanged; the model
still followed a forged bracketed citation embedded inside document text in
both formats, which is why that limitation is documented above rather than
claimed as fixed. Live-provider results are still not published.

Upgrade path and limits: the supported source version for adoption is
`v0.3.1`. Downgrade is restore from backup only; there are no automatic
down-migrations. Maintenance requires stopping API, Worker, MCP and CLI
consumers before running the migration host, taking a backup first, since
migration `0007` is not reversible once it commits.
