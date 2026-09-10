# Evaluations

The starter kit includes a repeatable evaluation workflow for local RAG and answer-quality checks. API and CLI entry points both call the same Application command, `StartEvaluationRunCommand`.

## Entry Points

API:

```http
POST /api/v1/evaluations/runs -> 200 OK with the completed run
GET /api/v1/evaluations/runs/{runId}
GET /api/v1/evaluations/runs/{runId}/summary
```

For the current starter-kit scope, `POST /api/v1/evaluations/runs` is synchronous: the request thread creates the run, executes every case, persists the terminal status, and then returns the completed run body. The GET endpoints are for reading the persisted run or summary after the POST returns. Cancellation completes the run as `Canceled` after any already-recorded case results, leaving the run inspectable.

CLI:

```powershell
dotnet run --project src/GenAIPlatform.Evaluations -- run
```

The default local configuration uses mock model and embedding providers. Automated tests do not call real providers by default.

## Case Shape

Sample cases are embedded from `src/GenAIPlatform.Application.Evaluations/Seeds/evaluation-cases.v1.json`.

```json
{
  "version": "sample-v1",
  "cases": [
    {
      "id": "eval-001",
      "name": "Architecture description stays grounded",
      "question": "What architecture approach does this starter kit use?",
      "context": "[1] The starter kit uses Clean Architecture with Application-owned use cases.",
      "checks": [
        { "type": "required_phrase", "phrase": "Clean Architecture" },
        { "type": "forbidden_phrase", "phrase": "real customer secret" }
      ]
    }
  ]
}
```

Supported check types:

- `retrieval`: passes when the retrieved chunk count is at least `minimumHits`.
- `citation`: passes when the answer contains required citation references, defaulting to `[1]`.
- `required_phrase`: passes when the answer contains the phrase, case-insensitively.
- `forbidden_phrase`: passes when the answer does not contain the phrase.

Dataset loading rejects blank case IDs, names and questions, empty or whitespace-only `phrase` values for `required_phrase` and `forbidden_phrase` checks, invalid retrieval thresholds and citation checks with blank references. Validation errors identify the dataset, case and check type without logging answer content.

Sample cases include safe fixture `context` so local mock-provider runs are deterministic before or after a developer has indexed documents. When fixture context is present, it is the answer context for that case and retrieval is bypassed: no readiness check, embedding request or vector search is performed, and the request log contains no retrieved document references or embedding metadata. Dataset validation rejects `retrieval` checks on fixture-context cases because there are no retrieved chunks to count.

Retrieval-backed cases build their prompt context with the same `RagPromptBuilder` the RAG chat path uses, so an evaluation prompt carries the same `<source id title file>` frames, the same attribute escaping, the same `<source` and `</source>` marker neutralization inside document text and the same budget accounting including framing overhead, bounded by `GenAIPlatform:Rag:MaxContextCharacters`. Each request-log document reference takes its `referenceId` from the matching citation, so reference ids equal the `id` values in the framed prompt. Fixture context is unchanged: it bypasses retrieval and is used raw apart from trimming to the same character bound.

## Run Metadata

Persisted runs store:

- run ID;
- dataset version;
- runner version;
- prompt version;
- model;
- model settings;
- retrieval configuration;
- per-case latency;
- per-case estimated cost and currency.

The evaluation model call goes through `AiModelRequestLoggingService`, so evaluation calls are logged in `genai.ai_request_logs` with prompt metadata, usage, latency and estimated cost. Retrieval metadata is stored when the case used retrieval. Retrieval checks count retrieved chunks, while request-log document references record only chunks included in the trimmed evaluation prompt context. Fixture-context cases intentionally log no retrieval references or embedding metadata.

Failed model, embedding or retrieval calls produce failed case results with an error code.

## Summary

The summary endpoint returns:

- total cases;
- pass/fail counts;
- retrieval hit rate;
- average latency;
- average cost;
- failed case details.

Example:

```http
GET /api/v1/evaluations/runs/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/summary
```

## Sample Run

```powershell
$run = Invoke-RestMethod `
  -Method Post `
  -Uri http://localhost:5198/api/v1/evaluations/runs `
  -Headers @{ "X-Demo-User-Id" = "alice"; "X-Demo-Tenant-Id" = "local" } `
  -ContentType "application/json" `
  -Body '{"datasetVersion":"sample-v1","correlationId":"demo-eval-1"}'

Invoke-RestMethod `
  -Method Get `
  -Uri "http://localhost:5198/api/v1/evaluations/runs/$($run.runId)/summary" `
  -Headers @{ "X-Demo-User-Id" = "alice"; "X-Demo-Tenant-Id" = "local" }
```

## Retrieval Baseline

The retrieval baseline is a second, narrower evaluation: it measures retrieval quality only. It runs a frozen labeled dataset through the production retrieval port (`IRagVectorSearchStore`) against a real PostgreSQL/pgvector database, scores recall and ranking, and compares the result against gates stored in the dataset. It performs no model completion, uses no LLM judge, needs no schema change and does not change the `run` verb or the evaluation API.

Its purpose is regression detection. Later retrieval work has a fixed reference point to compare against instead of an opinion about whether results look better.

### Command

```powershell
$env:ConnectionStrings__GenAIPlatform = "Host=localhost;Port=5432;Database=genai_platform;Username=genai;Password=<password>"

dotnet run --project src/GenAIPlatform.Evaluations -- retrieval-baseline `
  --output docs/evaluations/retrieval-baseline-v1.json `
  --revision (git rev-parse HEAD) `
  --GenAIPlatform:Embeddings:MockVariant=Lexical `
  --GenAIPlatform:Embeddings:MockDimensions=1024
```

Options:

- `--output <path>`: report file to write. Defaults to `retrieval-baseline-report.json` in the current directory.
- `--revision <sha>`: code revision recorded in the report. Defaults to the `GIT_COMMIT` environment variable, then to `unknown`.

Exit codes: `0` when every gate is met, `1` when a gate is not met, `2` on a usage error. `1` is also returned when the corpus store is missing, unconfigured or unavailable; the sanitized reason is written to stderr and names no host, database, user or credential. The `run` verb is unchanged and still runs the answer-quality dataset.

The committed reference report is `docs/evaluations/retrieval-baseline-v1.json`. It was produced by the command above at revision `1937d92c68815f08c134704b410d73a2760ac3ae`. Regenerating it elsewhere reproduces `datasetHash`, `configuration.settingsHash`, every field under `aggregates` and every per-query field except `elapsedMilliseconds` exactly, on Windows and on Linux alike: the dataset digest is taken over line-ending-normalized bytes, so it does not depend on how the repository was checked out. `generatedAtUtc`, the `environment` versions and every timing field are expected to differ from run to run and from machine to machine.

### Benchmark Tenant Isolation

The dataset declares a tenant prefix (`retrieval-baseline`) and every corpus document and query tenant must start with it. Because that prefix is dataset-supplied input, every tenant must also start with the fixed literal `retrieval-baseline`: dataset validation rejects anything else, and the PostgreSQL corpus store repeats the fixed check before it issues its delete. A dataset naming `tenant-prod` is refused however permissive its declared prefix is, so the command can only address benchmark rows.

Each run deletes all `genai.documents` rows for exactly the tenants the dataset names (`retrieval-baseline` and `retrieval-baseline-other`), which cascades to their chunks and indexing jobs, and then inserts the frozen corpus again. Rows in any other tenant are never read, updated or deleted. Document and chunk ids are derived deterministically from the dataset ids, so repeated runs replace the same rows.

The command writes ordinary indexed document and chunk rows: `indexing_status` is `Indexed`, `storage_path` is a synthetic path under `retrieval-baseline/` that points at no real file, `content_hash` is a digest of the chunk text, and row timestamps are fixed so retrieval tie-breakers stay deterministic.

### Dataset Shape

The frozen dataset is embedded at `src/GenAIPlatform.Application.Evaluations/Seeds/retrieval-baseline.v1.json`:

```json
{
  "version": "retrieval-baseline-v1",
  "tenantPrefix": "retrieval-baseline",
  "gates": { "minRecallAtK": 0.9, "minNoMatchAccuracy": 1.0, "k": 3 },
  "corpus": [
    {
      "id": "doc-photosynthesis",
      "tenantId": "retrieval-baseline",
      "ownerUserId": "bench-alice",
      "accessLevel": "TenantPublic",
      "version": 1,
      "title": "Photosynthesis Notes",
      "fileName": "photosynthesis-notes.md",
      "chunks": [{ "id": "chunk-photosynthesis-1", "text": "..." }]
    }
  ],
  "queries": [
    {
      "id": "q-relevant-001",
      "question": "...",
      "tenantId": "retrieval-baseline",
      "userId": "bench-alice",
      "category": "relevant",
      "expectedDocumentIds": ["doc-photosynthesis"],
      "expectedChunkIds": ["chunk-photosynthesis-1"],
      "noMatch": false
    }
  ]
}
```

A corpus document may set `embeddingProvider` to force an incompatible provider on its chunks, or `embeddingModel` to force a different model of the same provider; either makes the document ineligible for retrieval, because the search filters on provider, model and dimensions together. A chunk may set `documentVersion` below the document version to become history that retrieval must ignore.

The frozen dataset holds 19 documents, 21 chunks and 34 queries across eight categories:

| Category | Queries | What it checks |
|---|---|---|
| `relevant` | 8 | A directly relevant document is retrieved. |
| `distractor` | 4 | A close sibling document does not outrank the right one. |
| `paraphrase` | 5 | A reworded question still retrieves its document. |
| `no-match` | 5 | An out-of-corpus question retrieves nothing. |
| `tenant-isolation` | 3 | A document in another tenant is never returned; its own tenant still retrieves it. |
| `private-ownership` | 3 | A private document owned by someone else is never returned; its owner still retrieves it. |
| `document-version` | 3 | Only current-version chunks are eligible; a history-only question retrieves nothing. |
| `embedding-compatibility` | 3 | Chunks embedded by a different provider, or by a different model of the same provider, are ignored. |

Queries that must retrieve nothing are labeled `noMatch`, whether the reason is an absent answer or a permission, version or compatibility filter. A query's label and its expectations must agree in both directions: `noMatch` is true exactly when the query expects no document, and a query in the `no-match` category must carry it. A query that expected nothing without being labeled would otherwise fall out of every metric denominator and leave the benchmark silently.

Dataset validation rejects unknown categories, duplicate document, chunk or query ids, blank questions, tenants outside the dataset prefix, tenants outside the fixed `retrieval-baseline` namespace, unknown access levels, chunk versions above their document version, documents without a current-version chunk, gates outside their range, `noMatch` queries that expect documents, unlabeled queries that expect no document, `no-match` queries that are not labeled `noMatch`, expected documents missing from the corpus, expected documents the query's caller cannot read, expected documents embedded with an incompatible provider or model, and expected chunks that are not current-version chunks of an expected document.

### Metrics

`K` is the chunk retrieval depth: the search returns the top `K` chunks, and documents are de-duplicated afterwards. Recall@K is therefore recall among the documents present in those top `K` chunks, not the top `K` documents. Two chunks of the same document occupy two of the `K` chunk slots but one rank position, so a document can be pushed out of the measured set by another document's chunks.

Scoring is at document granularity. Retrieved document ids are de-duplicated while keeping retrieval order, so one document occupies at most one rank position no matter how many of its chunks matched.

- **Recall@K**: relevant documents found among the retrieved documents, divided by the number of labeled relevant documents. Duplicate expected ids count once.
- **First relevant rank**: 1-based position of the first relevant document, or null when none was retrieved.
- **Reciprocal rank**: `1 / rank`, or `0` when no relevant document was retrieved. **MRR** is the mean over recall eligible queries.
- **No-match accuracy**: share of `noMatch` queries whose retrieval returned nothing. It is reported as `1` when the dataset labels no such query.
- **Hit**: for a query with relevant documents, at least one relevant document was retrieved. For a `noMatch` query, nothing was retrieved.

Edge rules: queries that label no relevant document are excluded from the recall and MRR denominators and report null recall and null rank. Mean recall and MRR are `0` when no query is recall eligible. Nothing divides by zero on an empty run.

### Report

The report is indented JSON and is safe to commit. It contains dataset ids, hashes, settings, versions, ranks and metrics only. It never contains question text, document text, chunk text, embedding vectors, credentials or connection strings.

Fields:

- `datasetVersion`, `datasetHash`: the dataset and the SHA-256 digest of the embedded seed, taken after stripping a UTF-8 byte order mark and folding line endings to LF, so the digest identifies the dataset content rather than the checkout it was built from. A unit test pins the expected digest, so changing the seed without regenerating this report fails the build.
- `codeRevision`, `generatedAtUtc`.
- `configuration`: embedding provider, model, mock variant, dimensions, `topK`, `minSimilarityScore` and a `settingsHash` over all of them, so two reports can be checked for configuration drift in one comparison. The mock variant is reported and hashed in its canonical spelling (`Hash` or `Lexical`), so `lexical`, `LEXICAL` and `Lexical` are one configuration rather than three.
- `environment`: operating system, .NET runtime, PostgreSQL version and pgvector extension version.
- `aggregates`: query counts, `recallAtK`, `meanReciprocalRank`, `noMatchAccuracy` and per-category query and hit counts.
- `gates`, `gatesPassed`: each gate with its threshold, the measured value and whether it was met.
- `queries`: per query id, category, expected and retrieved document ids, recall, first relevant rank, hit and elapsed milliseconds.
- `timings`: total, corpus replace, mean and maximum query milliseconds. Timings are reported separately from quality and vary by machine; they are not a quality signal.

### Gates

The dataset freezes two gates, measured on the lexical mock at 1024 dimensions with the default similarity threshold of `0.2` and `k = 3`:

| Gate | Threshold | Measured |
|---|---|---|
| `recall_at_k` | 0.9 | 1.0 |
| `no_match_accuracy` | 1.0 | 1.0 |

The recall gate is set below the measured value on purpose, so ordinary noise does not fail a run while a real relevance regression does. Later retrieval work, including hybrid search, must keep these gates rather than lower them: a change that cannot hold `recall_at_k` at or above `0.9` and `no_match_accuracy` at `1.0` on this dataset is a regression, not a new baseline.

### Mock Embeddings and What This Does Not Prove

The baseline runs on a deterministic mock embedding provider. `GenAIPlatform:Embeddings:MockVariant` selects which one:

- `Hash` (the default, unchanged): a content-hash vector. Two texts are close only if they are nearly identical, so it is useless for relevance measurement.
- `Lexical`: signed feature hashing of lower-cased alphanumeric tokens of length two or more into `MockDimensions`, L2-normalized. Cosine similarity between two lexical vectors is a token-overlap measure.

The lexical variant exists so retrieval plumbing can be measured deterministically without a provider account or network access. It models no semantics: it has no stemming, no synonyms and no word order. A distractor in this dataset is lexically close, not semantically close, and a paraphrase still shares vocabulary with its document.

Read the resulting numbers as an engineering check that permission filtering, version filtering, embedding compatibility filtering, thresholding, ranking and de-duplication behave as specified, and that a change did not break them. Do not read them as evidence of semantic answer quality. Real semantic quality requires a real embedding provider and a dataset labeled against it, which is out of scope for this starter kit's default configuration.
