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
