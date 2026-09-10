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

The retrieval schema keeps the relational `real[]` embedding metadata for auditability and adds a pgvector `embedding_vector` column for retrieval. Existing rows are backfilled from `embedding_values` when `003-pgvector-retrieval.sql` is applied. Legacy rows with null, non-finite or zero-magnitude arrays are intentionally left with `embedding_vector = NULL` so one bad historical embedding cannot abort the schema upgrade. Those chunks are excluded from retrieval and the affected documents should be re-indexed to regenerate valid vectors. The schema uses dimension-specific partial HNSW indexes for the default mock embedding size and common 1536-dimension embeddings; other dimensions still work through exact pgvector search. The local compose stack uses `pgvector/pgvector:pg16`; deployments need a pgvector build that supports HNSW indexes.

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
