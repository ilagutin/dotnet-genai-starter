# RAG Pipeline

The RAG pipeline demonstrates secure, observable retrieval-augmented generation on .NET.

## Ingestion

```text
POST /api/v1/documents
-> validate file
-> create Document
-> create IndexingJob
-> return 202 Accepted
-> Worker extracts text
-> Worker chunks text
-> Worker creates embeddings
-> Worker stores chunks and vectors
-> Worker marks document indexed or failed
```

The default plain-text extensions are `.txt` and `.md`.
`GenAIPlatform:DocumentIngestion:AllowedExtensions` is the shared allowlist for
upload validation and extraction. Entries are trimmed and normalized to lowercase;
a configured list can restrict the defaults or add text aliases such as `.log`.
Each entry must be a dot followed by ASCII letters or digits, and the list cannot
be empty. Adding an extension does not add a binary parser: the same text decoder
and invalid UTF-8 rejection apply.

## Current Implementation

Document upload accepts `multipart/form-data`, stores the source file through the Infrastructure file-storage adapter, atomically creates document metadata and its first PostgreSQL-backed indexing job through `IDocumentMetadataRepository`. Worker lease, retry and atomic chunk-completion operations use `IIndexingJobRepository`.

`GenAIPlatform.Worker` claims pending jobs through the Application dispatcher, extracts text from the configured allowed extensions, chunks it with a versioned chunking profile, creates embeddings through `IEmbeddingClient` and persists chunks with embedding metadata. The default embedding provider is deterministic mock embeddings; OpenAI-compatible embeddings can be enabled through configuration.

The retrieval schema stores one embedding representation per chunk: the pgvector `embedding_vector` column, which is `NOT NULL`. Migration `0002-pgvector-retrieval` added that column beside the original relational `real[]` copy and backfilled it; migration `0007-single-embedding-column` removed the duplicate `embedding_values` column, so a chunk can no longer carry two representations that disagree. Both are applied by `dotnet run --project src/GenAIPlatform.Migrations -- migrate`. The schema creates partial HNSW indexes for the default mock embedding size and common 1536-dimension embeddings, but the production search query does not reach them: retrieval is exact pgvector search for every dimension today, for the reasons measured below. The local compose stack uses `pgvector/pgvector:pg16`; deployments need a pgvector build that supports HNSW indexes.

0002 deliberately left legacy rows with null, non-finite or zero-magnitude arrays at `embedding_vector = NULL` so that one bad historical embedding could not abort the schema upgrade. 0007 cannot do the same, because dropping the array from such a row would discard its only embedding. It therefore re-runs the 0002 backfill with the identical predicate and then fails closed in two cases: when a chunk still has no `embedding_vector`, and when a chunk's vector does not match its array. Both messages report only how many chunks and how many documents are affected, never chunk text, document ids or vectors, and both name the repair: re-index those documents to regenerate valid embeddings, or delete those chunks explicitly. The migration and its journal row commit together, so a failed run leaves the schema and the journal exactly as they were and the run can be repeated after the repair. The diagnostic query that lists the affected document ids is in the [quickstart](quickstart.md#upgrading-an-existing-database).

Retrieval compares query vectors only with chunks that were embedded by the same provider and model as the query embedding. If the configured embedding provider or model changes, documents should be re-indexed before those new embeddings are expected to participate in RAG retrieval.

## Chunking

Chunks must preserve:

- document ID;
- document version;
- chunk position;
- source/title metadata;
- chunking profile version;
- text hash.

Stable chunk IDs should be derived from document ID, document version, chunk position and text hash.

## Retrieval

```text
User question
-> validate request and retrieval readiness
-> create query embedding
-> resolve current user access
-> search pgvector with metadata/security filters
-> return top-K chunks
-> build prompt with allowed context only
-> call model gateway
-> return answer with citations
```

Access filters must be applied before prompt construction. The LLM is not a security boundary.

The RAG API is `POST /api/v1/chat/rag`. Request fields include:

- `message`;
- optional model gateway settings: `model`, `temperature`, `maxOutputTokens`, `correlationId`;
- retrieval settings: `topK`, `minSimilarityScore`, `documentIds`.

`documentIds` is a metadata filter, not an authorization override. Omitted `documentIds` means broad search across otherwise authorized documents. Explicit `documentIds: null`, `documentIds: []` and empty GUID values are rejected instead of being treated as an omitted filter. The PostgreSQL vector search still restricts rows to the current tenant and to tenant-public documents or private documents owned by the current user before any chunk text is added to the prompt.

The RAG handler checks retrieval configuration and schema readiness before creating the query embedding. A malformed retrieval connection string or schema missing the pgvector retrieval columns fails with a sanitized retrieval error before the user question is sent to an embedding or chat model provider.

During search, PostgreSQL schema failures map to `retrieval_schema_error` and
other PostgreSQL query failures to `retrieval_query_failed`. Other Npgsql
exceptions and timeouts map to `retrieval_unavailable`. Each mapped search
failure emits warning 5001, `RagSearchInfrastructureFailure`, with only the
closed error code and exception type. It includes no exception object, SQL,
query text, document content or vector. Cancellation and existing
`RagVectorSearchException` instances propagate unchanged without this warning.
Unexpected `ArgumentException` or `InvalidOperationException` instances also
propagate unchanged: the dispatcher records the original exception and stack
with event 3002, and the HTTP request fails with 500 rather than a retrieval
error code. A failed search never becomes an empty no-context result and never
reaches model completion.

RAG questions are rejected before retrieval if they exceed the lower of the model gateway input limit and the embedding input limit. The query sent to the embedding provider is the same validated question rendered into the prompt; the API does not silently embed only a truncated prefix.

Default RAG retrieval uses the current document version and excludes older chunk versions. The default `minSimilarityScore` is `0.2`. Callers may override it between `-1` and `1`, but lower thresholds intentionally broaden retrieval. The prompt builder also enforces the lower of `GenAIPlatform:Rag:MaxContextCharacters` and the remaining rendered model input budget, including system instructions and user-message template overhead, so top-K retrieval cannot send unbounded context to the model; citations are returned only for chunks that were included in the rendered prompt context.

### Measurements

The retrieval query was measured once, on a bounded synthetic corpus, to find out whether the
partial HNSW indexes the schema creates are actually used. They are not, for any of the shapes
measured. No SQL was changed as a result, because no candidate variant was demonstrated to help
without changing what the query returns.

Setup: `pgvector/pgvector:pg16` in a throwaway container (PostgreSQL 16.13, pgvector 0.8.2),
migrated with the packaged runner, then loaded with 400 synthetic documents and 40,000 chunks
across 5 tenants with mixed `Private` and `TenantPublic` access: 20,000 chunks at 16 dimensions
and 20,000 at 1536, with deterministic vectors, followed by `ANALYZE`. Settings were the image
defaults, `hnsw.ef_search = 40`, `work_mem = 4MB`, `shared_buffers = 128MB`, plus `jit = off` and
`plan_cache_mode = force_custom_plan` so the plan matches what Npgsql gets when it binds parameter
values on every execution. Each scenario ran `EXPLAIN (ANALYZE, BUFFERS)` over the SQL text
`PostgresRagSearchExecutor` builds, with the same parameter types, once as warmup and then five
times; the table reports the median of the five. No planner setting was forced to make an index
appear.

| Scenario | Dims | Partial HNSW index used | Chunk rows scanned | Rows after join | Shared buffers | Median ms |
|---|---|---|---|---|---|---|
| Broad tenant scope, topK 5, minScore 0.2 | 16 | no | 20,000 | 2,800 | 1,461 hit | 20.3 |
| Selective `documentIds` (3 documents), topK 5 | 16 | no | 300 | 300 | 42 hit | 0.4 |
| Low-selectivity tenant, topK 50, minScore 0.0 | 16 | no | 20,000 | 2,800 | 1,461 hit | 12.5 |
| Broad tenant scope, topK 5, minScore 0.2 | 1536 | no | 20,000 | 2,800 | 119,601 hit, 18,660 read | 189.8 |

At this measured 40,000-chunk corpus, every broad-scope plan is the same: a bitmap scan of
`ix_document_chunks_embedding_dimensions` over all chunks of that dimension, a hash join against
the tenant's documents, and a top-N heapsort. At smaller corpora the planner may choose a
sequential scan instead; the qualitative finding, that the HNSW index is not used, holds either
way. The `documentIds` scenario is a nested loop through `documents_pkey` and
`ix_document_chunks_document`, which is why it is two orders of magnitude cheaper: it never
touches rows outside the three named documents.

Two extra probes isolated the cause on the same corpus. A bare
`SELECT ... FROM genai.document_chunks WHERE embedding_dimensions = 16 AND embedding_vector IS NOT NULL ORDER BY embedding_vector::vector(16) <=> $1 LIMIT 5`
does use `ix_document_chunks_embedding_vector_16_hnsw`; a single measured run took 0.6 ms. Adding
only the deterministic tie-breakers the production query orders by (`chunk.position, chunk.id`
after the distance) is enough on its own for the planner to drop the index and fall back to the
same bitmap scan and heapsort; that run took 10.9 ms. Those two probes were run once each, to
identify the cause rather than to time it. The join to `genai.documents` for the tenant and access
filters and the `(1 - distance) >= minSimilarityScore` predicate work against the index scan as
well.

So the index is functional; the query shape does not let the planner reach it. Getting it used
would mean giving up the deterministic tie-breakers, accepting approximate top-K results, and
restructuring the permission filter, all of which change what retrieval returns. That is a
separate decision with its own evaluation, not a query rewrite: the measured numbers here justify
opening it, not making it. At starter-kit corpus sizes the exact scan is well inside the latency
the rest of a RAG request spends in the model gateway, and the 1536-dimension row is the one to
watch, since its cost is dominated by reading 20,000 full-width vectors rather than by comparing
them.

### Context framing

Each included chunk is rendered inside a backend-owned frame:

```text
<source id="1" title="Architecture Notes" file="architecture-notes.md">
chunk text
</source>
```

Frames are separated by one blank line and no preamble text is added. The format is code-level behavior of the prompt builder, not part of the prompt template.

- `title` and `file` keep the existing metadata normalization (whitespace collapsed, bounded to 120 characters) and are then XML-attribute escaped in the order `&`, `"`, `<`, `>`. Attribute values never contain a newline, so a frame tag is always a single line and document metadata cannot close it.
- Chunk text is trimmed and every case-insensitive `<source` or `</source` occurrence inside it is rewritten to `<\source` or `<\/source`, so document text cannot forge or close a frame. Nothing else is removed or rewritten: instruction-like text stays verbatim inside its frame. The rewrite is deterministic and uses no random nonce.
- The character budget covers the framing overhead, not only chunk text: the separator, opening tag, both newlines and the closing tag are charged against `GenAIPlatform:Rag:MaxContextCharacters` and the remaining rendered model input budget. A chunk is skipped with neither a frame nor a citation when its text is empty or whitespace-only, or when the remaining budget cannot fit at least one character of its text; the next chunk is still considered. Truncation happens after the marker rewrite and never splits a UTF-16 surrogate pair: when the cut would land between the two halves of a pair it backs off by one code unit. Text that already contains a lone surrogate is passed through as is.
- `id` values are sequential over included chunks only and equal the `referenceId` of the matching citation, so every frame in the prompt has exactly one citation and every citation has exactly one frame. Skipped chunks produce neither.

Framing labels retrieved document text as data for the model. It is not a security boundary and it does not make the model immune to prompt injection: a model can still follow instructions it reads inside a frame, and it can still write a bracketed citation id that document text suggests even though no frame with that id exists. The `citations` array in the response lists only real frames, so a consumer can detect a citation id in the answer that has no matching entry. The control that keeps private content out of a prompt is access filtering on tenant, owner, access level and metadata before prompt construction, described in `docs/security-model.md`.

## Response

RAG responses should include:

- `message`: answer text or the configured no-context fallback;
- `model`: resolved model name;
- `provider`: model provider name for generated answers, or `null` when no model call was made;
- `usage`: model token usage when a model response exists, otherwise `null`;
- `prompt`: prompt template metadata for generated answers, otherwise `null`;
- `correlationId`: request correlation ID;
- `noContext`: `true` when retrieval found no allowed context and no model call was made;
- `citations`: context chunks included in the prompt.

If no relevant allowed chunks are found, return a clear fallback answer instead of fabricating context.

Successful response shape:

```json
{
  "message": "The indexed document says ... [1]",
  "model": "mock-chat",
  "provider": "mock",
  "usage": {
    "inputTokens": 42,
    "outputTokens": 12,
    "totalTokens": 54
  },
  "prompt": {
    "templateName": "rag-chat",
    "version": "v1",
    "contentHash": "..."
  },
  "correlationId": "demo-rag-chat-1",
  "noContext": false,
  "citations": [
    {
      "referenceId": "1",
      "documentId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "chunkId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
      "documentVersion": 1,
      "chunkPosition": 0,
      "title": "Architecture Notes",
      "fileName": "architecture-notes.md",
      "similarityScore": 0.94
    }
  ]
}
```

No-context response shape:

```json
{
  "message": "I could not find relevant document context for that question.",
  "model": "mock-chat",
  "provider": null,
  "usage": null,
  "prompt": null,
  "correlationId": "demo-rag-chat-1",
  "noContext": true,
  "citations": []
}
```
