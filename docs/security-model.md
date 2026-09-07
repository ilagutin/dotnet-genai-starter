# Security Model

The core principle is simple: the LLM is not a security boundary.

The backend decides what data and tools are available. The model may summarize, reason and propose actions, but it must not enforce authorization or receive privileged credentials.

## Demo Auth

The starter kit uses:

- `IUserContext` in the application layer;
- demo/fake authentication for local development;
- headers, seeded users or configuration as demo identity sources.

Real auth providers such as Entra ID or ASP.NET Identity are future adapters, not requirements for the local sample path.

The API registers the demo header-based `IUserContext` only for `Development` by default. Development requests may omit headers and use the configured local `demo-user` defaults for the quickstart. Production API startup fails unless the API composition root registers a real foreground `IUserContext` adapter before the app starts. The Infrastructure project registers `IBackgroundUserContext` for Worker/system jobs, not a foreground API `IUserContext`, so the background identity cannot satisfy the API auth requirement by DI ordering. Non-production demo environments can explicitly opt in to demo headers; in that opt-in mode, the configured default user, tenant, roles and groups are ignored, the request must include an explicit `X-Demo-User-Id` to be treated as authenticated, and anonymous requests receive no default claims. Worker hosts explicitly map the background context for job processing and do not use HTTP demo headers.

Demo headers such as `X-Demo-User-Id`, `X-Demo-Tenant-Id` and `X-Demo-Roles` are caller-controlled sample inputs. They are useful for local walkthroughs, but they are not authentication and must not be trusted in deployed environments.

## Retrieval Access

Current document access model:

- tenant-public document;
- private document;

Shared-with-user and shared-with-group grants are future scope. Until durable grant metadata and matching retrieval filters exist for those scopes, the API, domain model and database schema reject shared access values.

Correct flow:

```text
Resolve current user access
-> apply filters in retrieval
-> retrieve allowed chunks only
-> send allowed context to LLM
```

The PostgreSQL vector search adapter enforces this boundary. Retrieval joins chunks to documents and filters by tenant, indexed status, access level, owner, requested document IDs and embedding provider/model compatibility before returning context to the RAG prompt builder.

Incorrect flow:

```text
Search all documents
-> send restricted chunks to LLM
-> ask the LLM not to reveal them
```

## Logging

- Full rendered prompt logging is disabled by default.
- Metadata logging is allowed: request ID, user ID, model, prompt version, tokens, cost, status, retrieved document IDs.
- If full prompt logging is ever enabled, it must require opt-in, redaction, encryption, retention policy and restricted access.
- Tool execution is controlled by backend policy. The model may propose tool calls, but it cannot execute tools directly and never receives infrastructure credentials. The `approveRiskyTools` request flag is demo-only simulated approval: it has no second principal and pre-approves later model-selected risky calls in that request after validation and policy checks. A successful approval-required execution is recorded as `SimulatedApproved` in the tool audit log.

## Tools

Tool execution must go through backend policy. Risky tools require simulated approval or must be rejected. Configured external MCP tools are all approval-gated. The LLM must not receive infrastructure credentials.
