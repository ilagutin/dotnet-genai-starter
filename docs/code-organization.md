# Code Organization and Self-Documenting Rules

These rules keep the production codebase easy to audit, refactor and hand over to another team. Treat them as engineering guardrails, not formatting ceremony. An exception is acceptable only when it is explicit, local and easier to defend than the split it avoids.

The automated gate scans authored C# under both `src/` and `tests/`. Generated suffixes and build
output are excluded deterministically, while authored `AssemblyInfo.cs` remains in scope. Production
files have a 400-line limit and test files have an 800-line limit. Production logical types remain
limited to 400 aggregate lines across partial declarations; duplicate and status-literal checks apply
only to production code. This recognizes that integration fixtures often need more readable setup than
production code, while still preventing either area from growing without review.

## Size Guardrails

- A production class should stay under 400 physical lines. If it exceeds that limit, the code should be split unless the file is a simple composition root, generated code, a framework-required shape, or another clearly justified exception.
- A test file should stay under 800 physical lines. Test fixtures and builders may be larger than production files when that keeps the scenario legible, but they still need extraction once they exceed the limit.
- A method should fit in one readable workflow step. Long methods should be split by intent, for example validation, state loading, policy decision, side effect, persistence and response mapping.
- A large handler is a design smell. A handler should orchestrate a use case; domain rules, provider-specific work, rendering, parsing, persistence details and reusable policies should live behind named collaborators.
- Do not hide complexity by extracting vague helpers. Prefer small methods and types named after the business or workflow concept they represent.

## Responsibility Boundaries

- A class should have one reason to change. If a file changes for unrelated endpoints, storage details, validation rules and response shaping, it is carrying too many responsibilities.
- API endpoint classes should stay thin. They may group closely related route mapping, but request handling, orchestration and business decisions belong in application use cases.
- Application handlers should coordinate ports and policies. They should not become a substitute for the pipeline, domain model or infrastructure adapters.
- Infrastructure adapters may contain provider or persistence details, but those details should not leak into application handlers, controllers or domain code.

## Endpoint Organization

- Use one mapper file per route group. A top-level API version mapper should compose feature mappers such as chat, documents, usage and evaluations.
- Endpoint lambdas should stay transport-focused: bind HTTP input, dispatch an application request and map the application result to HTTP.
- Do not let endpoint files own workflow policy, provider error taxonomy or reusable validation rules. Put those behind application policies or shared API error mappers.
- Request and response DTOs may live near endpoint mappers, but split them into separate files when the mapper stops reading as route composition.

## Validation Placement

- API validation should cover transport shape only, such as missing multipart content, malformed route values or invalid JSON shape.
- Business input validation belongs in the application use case, preferably in `Validator.cs` for non-trivial commands and queries.
- Shared request limits and model/RAG validation should live in named policies or validators, not inline in endpoint lambdas or large handlers.
- Do not duplicate the same validation rule across API, handler and infrastructure. Choose the earliest reliable layer and keep later checks defensive.

## File and Type Boundaries

- Use one entity per file: one class, record, struct, enum or interface.
- Private nested entities are permitted when they keep a test or framework-specific shape clear. Extract them when they obscure the owning type's responsibility.
- Do not bundle command, response, validator, options, result and helper records into one convenience file. Split them so review diffs and ownership remain obvious.
- Test fixtures, builders and test scenario files are scanned for the 800-line file limit. They are exempt from production-only logical-type, duplicate and status-literal analysis.

## Application Pipeline Layout

Organize use cases by feature and action. Let the folder carry the context and keep file names simple:

```text
GenAIPlatform.Application.Core/
  Dispatching/
  Security/
  Configuration/
  Health/
  Users/
  ModelClients/
  Embeddings/

GenAIPlatform.Application.Knowledge/
  Documents/
    Upload/
      Command.cs
      Handler.cs
      Normalizer.cs
      Validator.cs
      Response.cs
    GetStatus/
      Query.cs
      Handler.cs
      Response.cs
    ProcessIndexingJobs/
      Command.cs
      Handler.cs
      Lease/
      Embedding/
      Failure/
    ProcessStorageCleanup/
  Retrieval/
  Embeddings/

GenAIPlatform.Application.Generation/
  Chat/
    Direct/
    Rag/
  ModelGateway/
  Prompts/
    Rendering/
    Templates/

GenAIPlatform.Application.Agentic/
  Chat/
  Tools/
  Validation/

GenAIPlatform.Application.Evaluations/
  StartRun/
    Command.cs
    Handler.cs
    Normalizer.cs
    Validator.cs
    Cases/
    Context/

GenAIPlatform.Application.Usage/
  GetUsage/
```

Use `Query.cs` instead of `Command.cs` when the use case is read-only. Avoid repeating the full folder context in file names, such as `DirectChatCommand.cs`, when `Chat/Direct/Command.cs` already communicates the intent.

If the existing name does not fit the action folder, treat that as a naming problem first. Either rename the action or split the use case until the folder and type names describe one behavior.

For non-trivial commands and queries, add `Validator.cs` beside the request and handler. `Validator.cs` contains FluentValidation rules for request shape and early input policy only. When validation also needs to produce a normalized value object for the handler, add `Normalizer.cs` beside it and keep trimming, defaults and enum parsing there. Tiny query objects may omit a validator only when there is no meaningful input rule.

## Pipeline Behaviors

Application requests run through the internal dispatcher pipeline before the handler. Behaviors are registered in `Setup.cs` in outer-to-inner order.

- `DispatchLoggingBehavior` is outermost and records request lifecycle telemetry, including validation failures.
- `RequestValidationBehavior` runs all FluentValidation validators for the request type and throws `RequestValidationException` when rule failures exist.

Add a new behavior only for cross-cutting workflow concerns that should apply consistently across many request types. Keep feature-specific policy in the feature folder instead of hiding it in a global behavior.

## Handler Shape

A healthy handler reads as a short ordered workflow:

1. Validate use-case-specific invariants that are not already covered by a validator.
2. Load required state through application ports.
3. Apply policies and domain decisions.
4. Call external ports with cancellation and clear failure semantics.
5. Persist durable state intentionally.
6. Map the application result.

When a workflow crosses storage, persistence, provider calls, leases, retries, cleanup or authorization, document and test the important partial failure states. Do not return success after a required durable side effect failed unless there is a documented recovery invariant and a test proving it.

If a use case crosses storage, database, provider calls, retries, leases, cleanup or cancellation, the handler should not own all failure-state logic directly. Extract a named coordinator, policy or workflow service that makes those state transitions explicit.

## Provider and Infrastructure DTOs

- Keep external-provider DTOs inside Infrastructure adapter folders, but not as nested records inside the client class once there is more than one or two DTOs.
- Provider request/response DTOs should be named after the external protocol, not after application contracts.
- Application and Domain types should not expose provider DTOs or provider-specific error shapes.
- Normalize provider errors at the adapter boundary before they reach application handlers.

## Infrastructure Error Boundary

Infrastructure adapters must catch infrastructure-specific exceptions at the port boundary and rethrow as an Application-layer exception declared in the port contract.

Examples:

- `PostgresException`, `NpgsqlException` and `TimeoutException` from Npgsql calls are caught in the repository adapter and rethrown as `ProviderException` or a concrete application exception such as `RagVectorSearchException`.
- `HttpRequestException`, `TaskCanceledException` and `JsonException` from `HttpClient` calls are caught in the provider client and rethrown as `AiModelException` or `EmbeddingClientException`.

Infrastructure exception types must not appear in `ApiExceptionHandler` switch cases except for bootstrap-time configuration errors, such as `PostgresConnectionConfigurationException` mapped to 500 during startup.

Rationale: the API exception handler depends only on Application and Domain exception contracts. Adding a new Infrastructure adapter must not require changes in the API layer.

## Workflow States and Error Mapping

- Do not represent internal workflow states as scattered strings. Use enums or value objects in Domain/Application and convert to strings only at API or persistence boundaries.
- Public error codes and HTTP status mapping should be centralized in API error mappers or application error contracts.
- Avoid local `switch` blocks that independently translate the same provider, retrieval, validation or tool states in multiple endpoints or handlers.
- When a string is required for persistence or public compatibility, define constants or a typed mapping close to the boundary.

## Dependency Registration

- Each service-owning `src/*` project exposes a single root `Setup.cs` as its DI entry point, with a public `AddX` extension method. Domain contains no service registration and has no `Setup.cs`. Where present, `Setup` also acts as the assembly marker; prefer `typeof(Setup).Assembly` for resource or assembly scans.
- Feature-level registration delegates live next to the feature as `<Feature>Setup.cs` (for example `ChatSetup.cs`, `AgenticSetup.cs`, `DocumentsSetup.cs`). The root `Setup.cs` composes these via feature-named extension methods such as `AddChatApplication`, `AddAgenticApplication` or `AddDocumentsApplication`.
- Hosts compose explicit per-module registrations instead of a root `AddApplication` method. This keeps memory-only hosts able to reference `Core + Knowledge` without pulling in chat generation.
- DI modules should register dependencies only; they should not contain business validation or runtime decision logic.
- Persistence and RAG collaborators are scoped. OpenAI model collaborators and its typed HTTP executor are transient; the `IAiModelClient` selector remains scoped. Composition supplies `TimeProvider.System` only when no clock was registered.
- Stable status strings are defined by seven explicit, exhaustive Domain enum mappings. Undefined values fail closed. The code gate exempts only their exact canonical paths from status-literal detection; size checks still apply.

## Self-Documenting Code

- Prefer explicit names over comments that explain what the code already says.
- Add short comments only for non-obvious concurrency, retry, transaction, security or recovery decisions.
- Remove duplication when it repeats workflow decisions or business rules. Local repetition in trivial mapping code is less harmful than a generic abstraction with unclear ownership.
- Avoid magic strings and scattered configuration keys when typed options, constants or policies already exist.
- Keep tests focused on behavior. Test setup should make the scenario clearer, not bury the reason for the assertion.

## Review Checklist

Before merging a change, check:

- Does any production class exceed 400 lines without a clear reason?
- Does any test file exceed 800 lines?
- Does any method mix unrelated workflow stages?
- Does each file contain one entity?
- Are command/query, handler, validator and response types placed under a feature/action folder?
- Can each handler be explained as orchestration rather than implementation detail?
- Are endpoint mappers split by route group and limited to HTTP concerns?
- Does non-trivial business validation live in validators or named policies?
- Are provider DTOs isolated from application contracts and split out of large clients?
- Are infrastructure exceptions normalized at the port boundary?
- Are internal workflow states typed instead of stringly-typed?
- Is public error mapping centralized?
- Is dependency registration still modular enough to review?
- Is duplicate behavior extracted behind a meaningful concept?
- Are partial failure states covered for non-transactional or external side effects?
