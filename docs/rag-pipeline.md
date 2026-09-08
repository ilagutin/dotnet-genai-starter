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

Supported formats:

- `.txt`
- `.md`

## Current Implementation

Document upload accepts `multipart/form-data`, stores the source file through the Infrastructure file-storage adapter, creates a document record and queues a PostgreSQL-backed indexing job.

`GenAIPlatform.Worker` claims pending jobs through the Application dispatcher, extracts text from `.txt` and `.md`, chunks it with a versioned chunking profile, creates embeddings through `IEmbeddingClient` and persists chunks with embedding metadata. The default embedding provider is deterministic mock embeddings; OpenAI-compatible embeddings can be enabled through configuration.

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
